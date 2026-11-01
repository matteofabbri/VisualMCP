using Microsoft.Extensions.DependencyInjection;
using Snitch.Languages.VB.ContextActions;
using Snitch.Languages.VB.Style;

namespace Snitch.Languages.VB;

/// <summary>
/// Registers the Visual Basic layer: the style service and the context actions. The shared
/// <c>AddSnitch</c> stays C#-only on purpose: a host that wants VB opts in with this call, so a
/// repository that only has C# never pays for the VB syntax assembly and a VB service can never shadow
/// the C# one in the container.
/// </summary>
public static class VbServiceCollectionExtensions
{
    public static IServiceCollection AddSnitchVisualBasic(this IServiceCollection services)
    {
        // Registered by its own interface, not as a second ISimplifyCodeService: the container holds one
        // instance per language and the caller names the language it wants.
        services.AddSingleton<VbSimplifyCodeService>();
        services.AddSingleton<IVbSimplifyCodeService>(provider => provider.GetRequiredService<VbSimplifyCodeService>());

        // context_action routes a .vb document here: the ids are the C# catalogue entries that have a VB
        // spelling, and the C#-only ones are refused with a written reason.
        services.AddSingleton<VbContextActionService>();
        services.AddSingleton<IVbContextActionService>(provider => provider.GetRequiredService<VbContextActionService>());
        return services;
    }
}
