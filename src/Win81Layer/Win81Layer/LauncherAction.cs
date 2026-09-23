using System;
using System.Threading.Tasks;

namespace Win81Layer;

public sealed record LauncherAction(string Id, string Label, string Glyph, Func<Task> Invoke, bool IsShareTarget = false);
