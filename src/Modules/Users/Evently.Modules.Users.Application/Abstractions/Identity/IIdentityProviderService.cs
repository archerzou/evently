using Evently.Common.Domain;

namespace Evently.Modules.Users.Application.Abstractions.Identity;

public interface IIdentityProviderService
{
    Task<Result<string>> RegisterUserAsync(UserModel user, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an identity. Used to compensate when a user was created in the identity
    /// provider but the corresponding local user could not be persisted. Never throws.
    /// </summary>
    Task<Result> DeleteUserAsync(string identityId, CancellationToken cancellationToken = default);
}
