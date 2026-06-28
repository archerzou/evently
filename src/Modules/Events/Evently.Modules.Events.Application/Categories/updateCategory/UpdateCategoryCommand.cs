using Evently.Modules.Events.Application.Abstractions.Messaging;

namespace Evently.Modules.Events.Application.Categories.updateCategory;

public sealed record UpdateCategoryCommand(Guid CategoryId, string Name) : ICommand;
