using Twig.Mcp.Tools;

namespace Twig.Mcp.Tests.Tools;

public abstract class NavigationToolsTestBase : ReadToolsTestBase
{
    protected NavigationTools CreateSut()
    {
        return new NavigationTools(BuildResolver(DefaultConfig));
    }
}
