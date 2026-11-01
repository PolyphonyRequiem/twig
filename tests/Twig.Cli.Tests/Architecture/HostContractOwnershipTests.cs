using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Shouldly;
using Twig.Commands;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Architecture;

public sealed class HostContractOwnershipTests
{
    [Theory]
    [InlineData("twig")]
    [InlineData("Twig.Infrastructure")]
    public void Compiled_native_assembly_has_no_legacy_domain_dependency(string assemblyName)
    {
        var baselineDirectory = Environment.GetEnvironmentVariable("TWIG_OWNERSHIP_ASSEMBLY_DIR");
        var assemblyPath = baselineDirectory is null
            ? assemblyName == "twig" ? typeof(EditorLauncher).Assembly.Location : typeof(TwigConfiguration).Assembly.Location
            : Path.Combine(baselineDirectory, assemblyName + ".dll");
        File.Exists(assemblyPath).ShouldBeTrue("the real production assembly must exist");
        if (baselineDirectory is not null)
            FileVersionInfo.GetVersionInfo(assemblyPath).ProductVersion.ShouldNotBeNull()
                .EndsWith("+cea0ee45e57e9312efdf307677b2bf553de270ee", StringComparison.Ordinal).ShouldBeTrue(
                    "the red fixture must be the exact phase-5 production binary");
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        pe.HasMetadata.ShouldBeTrue();
        var metadata = pe.GetMetadataReader();
        metadata.GetString(metadata.GetAssemblyDefinition().Name).ShouldBe(assemblyName);
        metadata.AssemblyReferences.Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .ShouldNotContain("Twig.Domain");
        metadata.TypeReferences.Select(handle => metadata.GetString(metadata.GetTypeReference(handle).Namespace))
            .ShouldNotContain(value => value == "Twig.Domain" || value.StartsWith("Twig.Domain.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Twig.Commands", "ConsoleInput", "IConsoleInput", "Twig.Commands", "twig")]
    [InlineData("Twig.Commands", "EditorLauncher", "IEditorLauncher", "Twig.Commands", "twig")]
    [InlineData("Twig.Infrastructure.Telemetry", "TelemetryClient", "ITelemetryClient",
        "Twig.Infrastructure.Telemetry", "Twig.Infrastructure")]
    public void Compiled_implementation_uses_host_owned_contract(string implementationNamespace,
        string implementationName, string contractName, string expectedNamespace, string expectedAssembly)
    {
        var baselineDirectory = Environment.GetEnvironmentVariable("TWIG_OWNERSHIP_ASSEMBLY_DIR");
        var assemblyPath = baselineDirectory is null
            ? implementationName == "TelemetryClient"
                ? typeof(TwigConfiguration).Assembly.Location
                : typeof(EditorLauncher).Assembly.Location
            : Path.Combine(baselineDirectory,
                implementationName == "TelemetryClient" ? "Twig.Infrastructure.dll" : "twig.dll");
        File.Exists(assemblyPath).ShouldBeTrue("the production assembly must exist");
        var bytes = File.ReadAllBytes(assemblyPath);
        if (baselineDirectory is not null)
            FileVersionInfo.GetVersionInfo(assemblyPath).ProductVersion.ShouldNotBeNull()
                .EndsWith("+cea0ee45e57e9312efdf307677b2bf553de270ee", StringComparison.Ordinal).ShouldBeTrue(
                    "each red fixture assembly must be from the exact phase-5 production build");

        using var stream = new MemoryStream(bytes);
        using var pe = new PEReader(stream);
        pe.HasMetadata.ShouldBeTrue();
        var metadata = pe.GetMetadataReader();
        var implementation = metadata.TypeDefinitions
            .Select(metadata.GetTypeDefinition)
            .Single(type => metadata.GetString(type.Namespace) == implementationNamespace
                && metadata.GetString(type.Name) == implementationName);
        var contracts = implementation.GetInterfaceImplementations()
            .Select(handle => metadata.GetInterfaceImplementation(handle).Interface)
            .Select(handle => ReadContract(metadata, handle)).ToArray();
        var contract = contracts.Single(value => value.Name == contractName);
        contract.Namespace.ShouldBe(expectedNamespace);
        contract.Assembly.ShouldBe(expectedAssembly);
    }

    private static (string Name, string Namespace, string Assembly) ReadContract(
        MetadataReader metadata, EntityHandle handle)
    {
        if (handle.Kind == HandleKind.TypeDefinition)
        {
            var type = metadata.GetTypeDefinition((TypeDefinitionHandle)handle);
            return (metadata.GetString(type.Name), metadata.GetString(type.Namespace),
                metadata.GetString(metadata.GetAssemblyDefinition().Name));
        }
        handle.Kind.ShouldBe(HandleKind.TypeReference);
        var reference = metadata.GetTypeReference((TypeReferenceHandle)handle);
        reference.ResolutionScope.Kind.ShouldBe(HandleKind.AssemblyReference);
        return (metadata.GetString(reference.Name), metadata.GetString(reference.Namespace),
            metadata.GetString(metadata.GetAssemblyReference(
                (AssemblyReferenceHandle)reference.ResolutionScope).Name));
    }
}
