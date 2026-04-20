using NSubstitute;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Config;
using Twig.Mcp.Tools;

namespace Twig.Mcp.Tests.Tools;

public abstract class ContextToolsTestBase : ReadToolsTestBase
{
    private static readonly TwigConfiguration DefaultConfig = new()
    {
        Display = new DisplayConfig { CacheStaleMinutes = 5 },
    };

    protected ContextTools CreateSut()
    {
        return new ContextTools(BuildResolver(DefaultConfig));
    }
}
