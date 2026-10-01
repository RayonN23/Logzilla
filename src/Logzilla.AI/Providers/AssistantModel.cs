namespace Logzilla.AI.Providers;

/// <summary>
/// One model a provider offers this user. <paramref name="Note"/> is a short
/// qualifier for the picker, such as a cost multiplier; empty when there is none. <paramref name="Multiplier"/>
/// is GitHub Copilot's premium-request multiplier, when the provider has one.
/// </summary>
public sealed record AssistantModel(string Id, string Name, string Note = "", double? Multiplier = null);
