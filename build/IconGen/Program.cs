using System.IO;
using ShotIt.Rendering;

// Regenerates the application icon from the vector logo: dotnet run --project build/IconGen -- <output folder>
string folder = args.Length > 0 ? args[0] : ".";
var thread = new Thread(() =>
{
    Directory.CreateDirectory(folder);
    File.WriteAllBytes(Path.Combine(folder, "shotit.ico"), AppIcon.RenderIco());
    File.WriteAllBytes(Path.Combine(folder, "shotit-256.png"), AppIcon.RenderPng(256));
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
Console.WriteLine("Icon written to " + Path.GetFullPath(folder));
