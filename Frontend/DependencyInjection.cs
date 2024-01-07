using BlazorTable;
using MatBlazor;

using Microsoft.Extensions.DependencyInjection;

namespace Frontend;

public static class DependencyInjection
{
    public static IServiceCollection AddFrontend(this IServiceCollection services)
    {
        services.AddMatBlazor();

        services.AddRazorPages();

        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        services.AddBlazorTable();

        return services;
    }
}
