using System;
using System.Runtime.InteropServices;

namespace KeyRingGuard.SdkFixture;

// Exercise the native entry point used by the MSBuild SDK resolver without
// installing SDKs or executing assemblies from the synthetic SDK directories.
public static class SdkResolverProbe
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Result(int key, IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Error(IntPtr message);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr SetErrorWriter(Error writer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Resolve(IntPtr root, IntPtr directory, int flags, Result result);

    public static string Select(string library, string root, string directory, bool disallowPreview)
    {
        var handle = NativeLibrary.Load(library);
        bool windows = OperatingSystem.IsWindows();
        IntPtr rootPointer = windows ? Marshal.StringToCoTaskMemUni(root) : Marshal.StringToCoTaskMemUTF8(root);
        IntPtr directoryPointer = windows ? Marshal.StringToCoTaskMemUni(directory) : Marshal.StringToCoTaskMemUTF8(directory);
        string selected = null;
        Result result = (key, value) =>
        {
            if (key == 0)
                selected = windows ? Marshal.PtrToStringUni(value) : Marshal.PtrToStringUTF8(value);
        };
        Error error = _ => { };
        var setError = Marshal.GetDelegateForFunctionPointer<SetErrorWriter>(NativeLibrary.GetExport(handle, "hostfxr_set_error_writer"));
        IntPtr previous = setError(error);
        try
        {
            var resolve = Marshal.GetDelegateForFunctionPointer<Resolve>(NativeLibrary.GetExport(handle, "hostfxr_resolve_sdk2"));
            int code = resolve(rootPointer, directoryPointer, disallowPreview ? 1 : 0, result);
            if (code != 0 && selected != null)
                throw new InvalidOperationException("Failed SDK resolution returned a selected SDK.");
            return selected;
        }
        finally
        {
            setError(previous == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<Error>(previous));
            GC.KeepAlive(error);
            GC.KeepAlive(result);
            Marshal.FreeCoTaskMem(rootPointer);
            Marshal.FreeCoTaskMem(directoryPointer);
            NativeLibrary.Free(handle);
        }
    }
}
