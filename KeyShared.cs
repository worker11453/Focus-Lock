using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace FocusLock
{
    // Key 与 KeyGui 共用：读取状态、结束 FocusLock 进程。
    internal static class KeyTool
    {
        public static LockState ReadDiskState()
        {
            try
            {
                if (!File.Exists(AppPaths.StateFile))
                {
                    return LockState.Empty();
                }
                LockState st;
                if (LockState.TryParse(File.ReadAllText(AppPaths.StateFile, Encoding.UTF8), out st))
                {
                    return st;
                }
            }
            catch
            {
            }
            return LockState.Empty();
        }

        public static int StopFocusLock()
        {
            int killed = 0;
            try
            {
                foreach (Process p in Process.GetProcesses())
                {
                    try
                    {
                        string name = p.ProcessName;
                        if (name.IndexOf("FocusLock", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }
                        string path = null;
                        try
                        {
                            path = p.MainModule.FileName;
                        }
                        catch
                        {
                        }
                        if (path != null &&
                            !Locker.PathsEqual(Path.GetDirectoryName(path), AppPaths.ExeDir))
                        {
                            continue;
                        }
                        p.Kill();
                        killed++;
                        Log.Write("Key 结束进程: " + name + "(" + p.Id + ")");
                    }
                    catch
                    {
                    }
                    finally
                    {
                        try
                        {
                            p.Dispose();
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }
            return killed;
        }
    }
}
