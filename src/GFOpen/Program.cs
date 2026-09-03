using System.Diagnostics;
using System.IO;
using System.IO.Pipes;

// GFOpen: the fast path for opening a folder from a taskbar pin or a shortcut double-click.
// It forwards the request to the already-running Glass Folders tray over its named pipe and exits.
// Because it's a tiny console app (no WPF), it starts in a fraction of the time the full app needs,
// which is what makes a taskbar-pinned folder open feel instant. If the tray isn't running, it
// launches the full app to handle the request (and start the tray).

const string PipeName = "GlassFolders.Pipe";

string? name = null;
var files = new List<string>();
string[] argv = Environment.GetCommandLineArgs(); // [0] = this exe
for (int i = 1; i < argv.Length; i++)
{
    string a = argv[i];
    if (a.Equals("--open", StringComparison.OrdinalIgnoreCase) && i + 1 < argv.Length)
        name = argv[++i];
    else if (name == null && !a.StartsWith("--", StringComparison.Ordinal))
        name = a;                 // allow "GFOpen.exe <FolderName>"
    else
        files.Add(a);             // dropped files, forwarded through
}

if (string.IsNullOrWhiteSpace(name))
    return; // nothing to open

// Message format matches App.BuildMessage: "OPEN\n<name>\n<file>\n<file>...".
string message = "OPEN\n" + name + (files.Count > 0 ? "\n" + string.Join("\n", files) : "");

if (!TryForward(message))
    LaunchFullApp(name, files);

static bool TryForward(string message)
{
    try
    {
        using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
        client.Connect(1200);
        using var writer = new StreamWriter(client) { AutoFlush = true };
        writer.Write(message);
        return true;
    }
    catch
    {
        return false; // tray not running (or pipe busy) -> fall back
    }
}

static void LaunchFullApp(string name, List<string> files)
{
    try
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "GlassFolders.exe");
        if (!File.Exists(exe)) return;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.ArgumentList.Add("--open");
        psi.ArgumentList.Add(name);
        foreach (var f in files) psi.ArgumentList.Add(f);
        Process.Start(psi);
    }
    catch { }
}
