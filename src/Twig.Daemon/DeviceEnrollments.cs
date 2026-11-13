using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bench.Domain;

namespace Bench.Server;

public sealed record DeviceRegistration(Guid DeviceId, string PublicKey);
public sealed record DeviceReceipt(Guid DeviceId, DateTimeOffset Expires);
public sealed record DeviceChallengeRequest(Guid DeviceId, Guid ViewId);
public sealed record DeviceChallenge(string Message);
public sealed record DeviceProof(Guid DeviceId, Guid ViewId, string Message, string Signature);
internal sealed record RememberedDevice(Guid DeviceId, Guid ProfileId, string PublicKey, string Scope,
    DateTimeOffset Expires);

// The browser retains a non-exportable signing key, not a reusable content bearer.
public sealed class DeviceEnrollments(string directory, string origin, TimeProvider? clock = null)
{
    private readonly object gate = new();
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Dictionary<string, (Guid Device, Guid View, DateTimeOffset Expires)> challenges = [];

    public PairResult EnrollWindows(WindowsEnrollment body, CanvasApproval? approval, CatalogSnapshot catalog, ViewSessions sessions)
    {
        lock (gate)
        {
            var profile = body.ProfileId;
            if (File.Exists(PathFor(body.DeviceId)))
            {
                var prior = Read(body.DeviceId, approval, catalog);
                if (prior.PublicKey != body.PublicKey)
                    throw new HttpFailure(409, "DeviceConflict", "This enrollment identifier belongs to another key.");
                profile = prior.ProfileId;
            }
            var paired = sessions.ResumeDevice(body.ViewId, body.DeviceId, profile, catalog);
            var session = sessions.Content(paired.ViewId, paired.ContentCapability, true);
            _ = Register(session, new(body.DeviceId, body.PublicKey), catalog);
            return paired;
        }
    }

    public DeviceReceipt Register(ViewSession session, DeviceRegistration registration, CatalogSnapshot catalog)
    {
        lock (gate)
        {
            var scope = Scope(session, catalog);
            if (registration.DeviceId == Guid.Empty) Reject();
            using var key = ReadKey(registration.PublicKey);
            Directory.CreateDirectory(directory);
            if (File.Exists(PathFor(registration.DeviceId)))
            {
                var prior = Read(registration.DeviceId, session.Approval, catalog);
                if (prior.PublicKey != registration.PublicKey || prior.ProfileId != session.ProfileId)
                    throw new HttpFailure(409, "DeviceConflict", "This enrollment identifier belongs to another key or profile.");
                session.DeviceId = prior.DeviceId;
                return new(prior.DeviceId, prior.Expires);
            }
            if (Directory.GetFiles(directory, "*.json").Length >= 64)
                throw new HttpFailure(429, "DeviceLimit", "Forget an enrolled device before adding another.");
            var device = new RememberedDevice(registration.DeviceId, session.ProfileId, registration.PublicKey, scope,
                time.GetUtcNow().AddDays(30));
            var path = PathFor(device.DeviceId);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(file, device, DeviceJson.Default.RememberedDevice);
                    file.Flush(true);
                }
                File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            session.DeviceId = device.DeviceId;
            return new(device.DeviceId, device.Expires);
        }
    }

    public DeviceChallenge Challenge(DeviceChallengeRequest request, CanvasApproval? approval, CatalogSnapshot catalog)
    {
        lock (gate)
        {
            ValidateOrigin();
            var device = Read(request.DeviceId, approval, catalog);
            if (request.ViewId == Guid.Empty) Reject();
            var now = time.GetUtcNow();
            foreach (var expired in challenges.Where(entry => entry.Value.Expires <= now).Select(entry => entry.Key).ToArray())
                challenges.Remove(expired);
            if (challenges.Count >= 64)
                throw new HttpFailure(429, "ChallengeLimit", "Wait one minute before trying to connect again.");
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var message = $"twig-device-v1\n{origin}\n{device.DeviceId:D}\n{request.ViewId:D}\n{device.Scope}\n{nonce}";
            challenges.Add(message, (device.DeviceId, request.ViewId, now.AddMinutes(1)));
            return new(message);
        }
    }

    public PairResult Resume(DeviceProof proof, CanvasApproval? approval, CatalogSnapshot catalog, ViewSessions sessions)
    {
        lock (gate)
        {
            ValidateOrigin();
            if (proof.Message is null) Reject();
            if (!challenges.Remove(proof.Message, out var pending)
                || pending.Device != proof.DeviceId || pending.View != proof.ViewId || pending.Expires <= time.GetUtcNow())
                Reject();
            var device = Read(proof.DeviceId, approval, catalog);
            using var key = ReadKey(device.PublicKey);
            byte[] signature;
            try { signature = Convert.FromBase64String(proof.Signature); }
            catch (FormatException) { throw Rejected(); }
            if (signature.Length != 64 || !key.VerifyData(Encoding.UTF8.GetBytes(proof.Message), signature,
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                Reject();
            return sessions.ResumeDevice(proof.ViewId, device.DeviceId, device.ProfileId, catalog);
        }
    }

    public Guid Forget(ViewSession session, CatalogSnapshot catalog)
    {
        lock (gate)
        {
            _ = Scope(session, catalog);
            var id = session.DeviceId ?? throw new HttpFailure(409, "DeviceNotEnrolled", "This view has no remembered device.");
            File.Delete(PathFor(id));
            foreach (var message in challenges.Where(entry => entry.Value.Device == id).Select(entry => entry.Key).ToArray())
                challenges.Remove(message);
            return id;
        }
    }

    private string Scope(ViewSession session, CatalogSnapshot catalog)
    {
        ValidateOrigin();
        if (!session.Canvas || session.ContentHash is null || session.Approval is not { } approval || !approval.Matches(catalog))
            throw new HttpFailure(403, "DeviceScopeDenied", "Only an explicitly paired, exact-source approved canvas can remember this device.");
        return Fingerprint(approval);
    }

    private RememberedDevice Read(Guid id, CanvasApproval? approval, CatalogSnapshot catalog)
    {
        if (id == Guid.Empty || approval is null || !approval.Matches(catalog)) Reject();
        var path = PathFor(id);
        if (!File.Exists(path)) Reject();
        using var file = File.OpenRead(path);
        if (file.Length > 4096) throw new InvalidDataException("Device enrollment exceeds its bound.");
        var device = JsonSerializer.Deserialize(file, DeviceJson.Default.RememberedDevice)
            ?? throw new InvalidDataException("Invalid device enrollment.");
        if (device.DeviceId != id || device.ProfileId == Guid.Empty || device.Expires <= time.GetUtcNow()
            || device.Scope != Fingerprint(approval!)) Reject();
        return device;
    }

    private void ValidateOrigin()
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "localhost"
            || uri.IsDefaultPort || uri.Port is < 1 or > 65535 || uri.AbsolutePath != "/" || uri.Query.Length != 0
            || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0 || origin != uri.GetLeftPart(UriPartial.Authority))
            throw new InvalidOperationException("Remembered devices require one stable HTTPS localhost origin.");
    }

    private static ECDsa ReadKey(string encoded)
    {
        if (encoded is not { Length: > 0 and <= 256 }) throw Rejected();
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw Rejected(); }
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read != bytes.Length || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                throw Rejected();
            return key;
        }
        catch (CryptographicException) { key.Dispose(); throw Rejected(); }
        catch { key.Dispose(); throw; }
    }

    private string PathFor(Guid id) => Path.Combine(directory, id.ToString("D") + ".json");
    private static string Fingerprint(CanvasApproval approval)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(approval.ToOperatorJson()))).ToLowerInvariant();
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Reject() => throw Rejected();
    private static HttpFailure Rejected() => new(403, "DeviceRejected", "This device cannot reconnect. Pair once to enroll it again.");
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(RememberedDevice))]
internal sealed partial class DeviceJson : JsonSerializerContext;
