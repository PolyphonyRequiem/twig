using Twig.Mcp.Tools;

namespace Twig.Mcp.Tests.Tools;

public abstract class ContextToolsTestBase : ReadToolsTestBase
{
    protected ContextTools CreateSut()
    {
        return new ContextTools(BuildResolver(DefaultConfig));
    }
}
