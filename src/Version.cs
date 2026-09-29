using System.Reflection;

// Launcher version metadata.
//
// Kept in step with Program.AppVersion BY HAND -- bump both together. This file is
// what makes the exe report a real version: without it the file properties read
// 0.0.0.0 and there is no way to tell which build a user is running.
[assembly: AssemblyTitle("DSH-App")]
[assembly: AssemblyProduct("DSH-App")]
[assembly: AssemblyCompany("DSH-App")]
[assembly: AssemblyDescription("DeepSeek Harness desktop client")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
