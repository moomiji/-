using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;

Console.WriteLine("Hello, World!");

WriteLine("- CurrentDirectory: {0}", Environment.CurrentDirectory);
WriteLine("- EnvironmentVariable: {0}", Environment.GetEnvironmentVariable("MAAFW_BINARY_PATH"));

WriteLine("- AssemblyLocation: {0}", GetAssemblyLocation());
WriteLine("- BaseDirectory: {0}", AppContext.BaseDirectory);

var libraryNames = new[]
{
    "MaaFramework",
    "MaaToolkit",
    "MaaAgentServer",
    "MaaAgentClient"
};
foreach (var name in libraryNames)
{
    var ret = NativeLibraryLoader.UseBindingResolverToLoad(name, null, out var libraryHandle, out var dllPath, out var resolver);
    WriteLine("Loading: {0}", name);
    WriteLine("  - ret: {0}", ret);
    WriteLine("  - resolver: {0}", resolver);
    WriteLine("  - handle: {0}", libraryHandle);
    WriteLine("  - dllPath: {0}", dllPath);
    var module = DyldHelper.PathFromHandle(libraryHandle);
    WriteLine("  -  module: {0}", module);
    libraryHandle = -1;
    dllPath = "-1";
    resolver = ApiInfoFlags.None;

}

[UnconditionalSuppressMessage("SingleFile", "IL3000: Avoid accessing Assembly file path when publishing as a single file",
    Justification = "The code handles the Assembly.Location being null/empty by falling back to AppContext.BaseDirectory.")]
string? GetAssemblyLocation() => typeof(NativeLibraryLoader).Assembly.Location;
void WriteLine([StringSyntax("CompositeFormat")] string format, object? arg0)
{
    if (arg0 is string str && str.Length == 0)
        arg0 = "EMPTY";
    arg0 ??= "NULL";
    Console.WriteLine(format, arg0);
}

public static class DyldHelper
{
    [DllImport("libSystem.dylib")]
    private static extern uint _dyld_image_count();

    [DllImport("libSystem.dylib")]
    private static extern IntPtr _dyld_get_image_name(uint idx);

    [DllImport("libSystem.dylib")]
    private static extern IntPtr dlopen(string path, int mode);

    [DllImport("libSystem.dylib")]
    private static extern int dlclose(IntPtr handle);

    private const int RTLD_LAZY = 0x1;

    public static string? PathFromHandle(IntPtr handle)
    {
        for (uint i = _dyld_image_count(); i-- > 0;)
        {
            IntPtr imageNamePtr = _dyld_get_image_name(i);
            if (imageNamePtr == IntPtr.Zero)
                continue;

            string imageName = Marshal.PtrToStringUTF8(imageNamePtr)!;
            IntPtr probeHandle = dlopen(imageName, RTLD_LAZY);
            dlclose(probeHandle);

            if (handle == probeHandle)
            {
                return imageName;
            }
        }
        return null;
    }
}


internal static partial class NativeLibraryLoader
{
    private static readonly Assembly s_assembly = typeof(NativeLibraryLoader).Assembly;


    internal static string LoadedDirectory = string.Empty;
    internal static readonly Dictionary<string, nint> LoadedLibraryHandles = [];

    internal static readonly List<string> SearchPaths = [];

    public static bool UseBindingResolverToLoad(string libraryName, DllImportSearchPath? searchPath, out nint libraryHandle, out string dllPath, out ApiInfoFlags resolver)
    {
        libraryName = GetFullLibraryName(libraryName);
        dllPath = GetLibraryPaths(libraryName).FirstOrDefault(File.Exists, string.Empty);
        resolver = ApiInfoFlags.UseDefaultResolver;

        if (!string.IsNullOrEmpty(dllPath) && NativeLibrary.TryLoad(dllPath, out libraryHandle))
        {
            resolver = ApiInfoFlags.UseBindingResolver;
            return true;
        }
        return NativeLibrary.TryLoad(libraryName, s_assembly, searchPath, out libraryHandle);
    }

    [UnconditionalSuppressMessage("SingleFile", "IL3000: Avoid accessing Assembly file path when publishing as a single file",
        Justification = "The code handles the Assembly.Location being null/empty by falling back to AppContext.BaseDirectory.")]
    private static IEnumerable<string> GetLibraryPaths(string libraryFullName)
    {
        // For single-file deployments, the assembly location is an empty string so we fall back
        // to AppContext.BaseDirectory which is the directory containing the single-file executable.
        var assemblyDirectory = string.IsNullOrEmpty(s_assembly.Location)
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(s_assembly.Location)!;

        string?[] basePaths =
        [
            ..SearchPaths,
            Environment.GetEnvironmentVariable("MAAFW_BINARY_PATH"),
            assemblyDirectory,
            Environment.CurrentDirectory,
        ];

        string[] runtimesPaths =
        [
            "./",
            $"./runtimes/{GetArchitectureName()}/native/",
        ];

        return from basePath in basePaths.Distinct()
               where !string.IsNullOrWhiteSpace(basePath)
               from runtimesPath in runtimesPaths
               let libraryPath = Path.Combine(basePath, runtimesPath, libraryFullName)
               select Path.GetFullPath(libraryPath);
    }

#pragma warning disable IDE0072 // 添加缺失的事例
    private static string GetArchitectureName() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 when IsWindows => "win-x64",
        // Architecture.Arm64 when IsWindows => "win-arm64",
        Architecture.X64 when IsLinux => "linux-x64",
        Architecture.Arm64 when IsLinux => "linux-arm64",
        Architecture.X64 when IsOSX => "osx-x64",
        Architecture.Arm64 when IsOSX => "osx-arm64",
        Architecture.X64 when IsAndroid => "android-x64",
        Architecture.Arm64 when IsAndroid => "android-arm64",
        _ => throw new PlatformNotSupportedException(),
    };
#pragma warning restore IDE0072 // 添加缺失的事例

    private static string GetFullLibraryName(string libraryName)
    {
        if (IsWindows)
            return $"{libraryName}.dll";
        if (IsLinux || IsAndroid)
            return $"lib{libraryName}.so";
        if (IsOSX)
            return $"lib{libraryName}.dylib";

        throw new PlatformNotSupportedException();
    }

    private static bool IsWindows => OperatingSystem.IsWindows();
    private static bool IsLinux => OperatingSystem.IsLinux();
    private static bool IsOSX => OperatingSystem.IsMacOS();
    private static bool IsAndroid => OperatingSystem.IsAndroid();
}


/// <summary>
///     Represents information about binding interoperable API.
/// </summary>
[Flags]
public enum ApiInfoFlags
{
    /// <summary>
    ///     No flags.
    /// </summary>
    None = 0,

    /// <summary>
    ///     Indicates that the API is in MaaFramework context.
    /// </summary>
    InFrameworkContext = 1,

    /// <summary>
    ///     Indicates that the API is in MaaAgentServer context.
    /// </summary>
    InAgentServerContext = 2,

    /// <summary>
    ///    Indicates that the API uses the default resolver.
    /// </summary>
    UseDefaultResolver = 1 << 8,

    /// <summary>
    ///    Indicates that the API uses the resolver from binding.
    /// </summary>
    UseBindingResolver = 2 << 8,
}

internal static class ApiInfoFlagsExtensions
{
    internal const ApiInfoFlags ContextMask = ApiInfoFlags.InFrameworkContext | ApiInfoFlags.InAgentServerContext;
    internal const ApiInfoFlags ResolverMask = ApiInfoFlags.UseDefaultResolver | ApiInfoFlags.UseBindingResolver;

    internal static bool HasFlag_Context(this ApiInfoFlags flags) => (flags & ContextMask) != ApiInfoFlags.None;
    internal static bool HasFlag_ResolverExcept(this ApiInfoFlags flags, ApiInfoFlags other) => (flags & ~other & ResolverMask) != ApiInfoFlags.None;
}
