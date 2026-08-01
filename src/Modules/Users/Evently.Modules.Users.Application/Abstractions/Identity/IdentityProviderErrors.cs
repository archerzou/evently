using Evently.Common.Domain;

namespace Evently.Modules.Users.Application.Abstractions.Identity;

public static class IdentityProviderErrors
{
    public static readonly Error EmailIsNotUnique = Error.Conflict(
        "Identity.EmailIsNotUnique",
        "The specified email is not unique.");

    public static readonly Error DeleteUserFailed = Error.Problem(
        "Identity.DeleteUserFailed",
        "Failed to delete the user from the identity provider.");
}
