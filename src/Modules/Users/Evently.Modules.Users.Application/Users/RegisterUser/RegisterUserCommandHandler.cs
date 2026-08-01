using Evently.Common.Application.Messaging;
using Evently.Common.Domain;
using Evently.Modules.Users.Application.Abstractions.Data;
using Evently.Modules.Users.Application.Abstractions.Identity;
using Evently.Modules.Users.Domain.Users;

namespace Evently.Modules.Users.Application.Users.RegisterUser;

internal sealed class RegisterUserCommandHandler(
    IIdentityProviderService identityProviderService,
    IUserRepository userRepository,
    IUnitOfWork unitOfWork)
    : ICommandHandler<RegisterUserCommand, Guid>
{
    public async Task<Result<Guid>> Handle(RegisterUserCommand request, CancellationToken cancellationToken)
    {
        Result<string> result = await identityProviderService.RegisterUserAsync(
            new UserModel(request.Email, request.Password, request.FirstName, request.LastName),
            cancellationToken);

        if (result.IsFailure)
        {
            return Result.Failure<Guid>(result.Error);
        }

        string identityId = result.Value;

        try
        {
            var user = User.Create(request.Email, request.FirstName, request.LastName, identityId);

            userRepository.Insert(user);

            await unitOfWork.SaveChangesAsync(cancellationToken);

            return user.Id;
        }
        catch (Exception)
        {
            // The identity was created but the local user could not be persisted.
            // Remove the identity so the caller can retry with the same email.
            // CancellationToken.None: compensation must run even if the request was canceled.
            await identityProviderService.DeleteUserAsync(identityId, CancellationToken.None);

            throw;
        }
    }
}
