using Evently.Common.Presentation.Endpoints;
using Evently.Modules.Ticketing.Application.Carts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Evently.Modules.Ticketing.Infrastructure;

public static class TicketingModule
{
    public static IServiceCollection AddTicketingModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddInfrastructure(configuration);

        services.AddEndpoints(Presentation.AssemblyReference.Assembly);

        return services;
    }

#pragma warning disable S1172, IDE0060 // 'configuration' is unused until this module's persistence is implemented
    private static void AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
#pragma warning restore S1172, IDE0060
    {
        services.AddSingleton<CartService>();
    }
}
