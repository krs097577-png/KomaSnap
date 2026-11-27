param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Target)

if (-not ('KomaSnapShaderCompiler' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class KomaSnapShaderCompiler
{
    [DllImport("d3dcompiler_47.dll", ExactSpelling = true)]
    static extern int D3DCompileFromFile([MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr defines, IntPtr includes, [MarshalAs(UnmanagedType.LPStr)] string entry,
        [MarshalAs(UnmanagedType.LPStr)] string profile, uint flags, uint effectFlags,
        out IntPtr shader, out IntPtr messages);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate IntPtr ReadBlobField(IntPtr blob);

    static byte[] ReadBlob(IntPtr blob)
    {
        IntPtr table = Marshal.ReadIntPtr(blob);
        var pointer = (ReadBlobField)Marshal.GetDelegateForFunctionPointer(
            Marshal.ReadIntPtr(table, 3 * IntPtr.Size), typeof(ReadBlobField));
        var size = (ReadBlobField)Marshal.GetDelegateForFunctionPointer(
            Marshal.ReadIntPtr(table, 4 * IntPtr.Size), typeof(ReadBlobField));
        byte[] bytes = new byte[size(blob).ToInt32()];
        Marshal.Copy(pointer(blob), bytes, 0, bytes.Length);
        return bytes;
    }

    public static void Compile(string source, string target)
    {
        IntPtr shader, messages;
        int result = D3DCompileFromFile(source, IntPtr.Zero, IntPtr.Zero, "main", "ps_2_0",
            0, 0, out shader, out messages);
        try
        {
            if (result < 0)
                throw new InvalidOperationException(messages == IntPtr.Zero ? "D3DCompile: 0x" +
                    result.ToString("X8") : Encoding.UTF8.GetString(ReadBlob(messages)).TrimEnd('\0'));
            File.WriteAllBytes(target, ReadBlob(shader));
        }
        finally
        {
            if (shader != IntPtr.Zero) Marshal.Release(shader);
            if (messages != IntPtr.Zero) Marshal.Release(messages);
        }
    }
}
'@
}
[KomaSnapShaderCompiler]::Compile($Source, $Target)

