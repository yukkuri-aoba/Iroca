param([int]$Pid_ = 17100, [string]$Out = "$PSScriptRoot\unity_main.png")
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text; using System.Collections.Generic;
public class W {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; }
  public static List<IntPtr> Find(uint pid){ var res=new List<IntPtr>(); EnumWindows((h,l)=>{ uint p; GetWindowThreadProcessId(h,out p); if(p==pid && IsWindowVisible(h)) res.Add(h); return true;}, IntPtr.Zero); return res; }
  public static string Title(IntPtr h){ var sb=new StringBuilder(512); GetWindowText(h,sb,512); return sb.ToString(); }
}
"@
$hs = [W]::Find([uint32]$Pid_)
$i = 0
foreach ($h in $hs) {
  $r = New-Object W+R; [void][W]::GetWindowRect($h, [ref]$r)
  $w = $r.Rt - $r.L; $ht = $r.B - $r.T
  $t = [W]::Title($h)
  "hwnd=$h title='$t' rect=$($r.L),$($r.T) ${w}x${ht}"
  if ($w -lt 200 -or $ht -lt 200) { continue }
  $bmp = New-Object System.Drawing.Bitmap $w, $ht
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $dc = $g.GetHdc()
  [void][W]::PrintWindow($h, $dc, 2)
  $g.ReleaseHdc($dc); $g.Dispose()
  $p = $Out -replace '\.png$', "_$i.png"
  $bmp.Save($p, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  "saved $p"
  $i++
}
