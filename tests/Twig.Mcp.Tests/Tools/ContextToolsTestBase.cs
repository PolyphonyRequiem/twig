using Twig.Mcp.Tools;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Shared test base for <see cref="ContextTools"/> (twig_set) MCP tool tests.
/// Inherits mock infrastructure from <see cref="ReadToolsTestBase"/> and adds
/// a <see cref="CreateSut()"/> factory for the <see cref="ContextTools"/> SUT.
/// </summary>
public abstract class ContextToolsTestBase : ReadToolsTestBase
{
    protected ContextTools CreateSut()
    {
        return new ContextTools(BuildResolver(DefaultConfig));
    }
}
