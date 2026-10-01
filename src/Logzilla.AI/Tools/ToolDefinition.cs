using System;

namespace Logzilla.AI.Tools;

/// <summary>
/// One tool a model may call: a name, what it is for, and the method behind it. The
/// method's parameters (with their [Description]s) become the tool's schema.
/// </summary>
public sealed record ToolDefinition(string Name, string Description, Delegate Method);
