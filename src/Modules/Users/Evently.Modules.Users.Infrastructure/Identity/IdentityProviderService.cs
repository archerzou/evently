using System.Net;
using Evently.Common.Domain;
using Evently.Modules.Users.Application.Abstractions.Identity;
using Microsoft.Extensions.Logging;

namespace Evently.Modules.Users.Infrastructure.Identity;

internal sealed class IdentityProviderService(KeyCloakClient keyCloakClient, ILogger<IdentityProviderService> logger)
    : IIdentityProviderService
{
    private const string PasswordCredentialType = "Password";

    // POST /admin/realms/{realm}/users
    public async Task<Result<string>> RegisterUserAsync(UserModel user, CancellationToken cancellationToken = default)
    {
        var userRepresentation = new UserRepresentation(
            user.Email,
            user.Email,
            user.FirstName,
            user.LastName,
            true,
            true,
            [new CredentialRepresentation(PasswordCredentialType, user.Password, false)]);

        try
        {
            string identityId = await keyCloakClient.RegisterUserAsync(userRepresentation, cancellationToken);

            return identityId;
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
        {
            logger.LogError(exception, "User registration failed");

            return Result.Failure<string>(IdentityProviderErrors.EmailIsNotUnique);
        }
    }

    // DELETE /admin/realms/{realm}/users/{id}
    public async Task<Result> DeleteUserAsync(string identityId, CancellationToken cancellationToken = default)
    {
        try
        {
            await keyCloakClient.DeleteUserAsync(identityId, cancellationToken);

            return Result.Success();
        }
        catch (HttpRequestException exception)
        {
            // Compensation must never mask the original failure, so this returns a result
            // instead of throwing. The orphaned identity is logged for manual follow-up.
            logger.LogError(
                exception,
                "Failed to delete identity {IdentityId} from the identity provider",
                identityId);

            return Result.Failure(IdentityProviderErrors.DeleteUserFailed);
        }
    }
}
