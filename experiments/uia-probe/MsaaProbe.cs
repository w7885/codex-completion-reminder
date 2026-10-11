// Alternative read-only Windows accessibility probe. No input injection.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using Accessibility;

internal static class MsaaProbe
{
    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder value, int length);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object accessible);
    [DllImport("oleacc.dll")] private static extern int AccessibleChildren(IAccessible parent, int start, int count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] object[] children, out int obtained);

    private sealed class Stats
    {
        public int Nodes, Radios, Cards;
        public bool Skip, Send;
    }
    private static int visited;
    [MTAThread]
    private static void Main()
    {
        int windows = 0, minimized = 0, radios = 0, cards = 0, roots = 0;
        bool unknown = false;
        string errorType = null;
        foreach (Process process in Process.GetProcessesByName("ChatGPT"))
        {
            using (process)
            {
                try
                {
                    IntPtr main = process.MainWindowHandle;
                    if (main == IntPtr.Zero || process.MainModule.FileName.IndexOf("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    windows++;
                    if (IsIconic(main)) minimized++;
                    List<IntPtr> handles = new List<IntPtr>();
                    handles.Add(main);
                    EnumChildWindows(main, delegate(IntPtr window, IntPtr ignored)
                    {
                        StringBuilder name = new StringBuilder(128);
                        GetClassName(window, name, name.Capacity);
                        if (name.ToString() == "Chrome_RenderWidgetHostHWND") handles.Add(window);
                        return true;
                    }, IntPtr.Zero);
                    foreach (IntPtr handle in handles)
                    {
                        Guid iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
                        object value;
                        if (AccessibleObjectFromWindow(handle, 0xFFFFFFFC, ref iid, out value) != 0) continue;
                        IAccessible root = value as IAccessible;
                        if (root == null) continue;
                        roots++;
                        visited = 0;
                        Stats stats = Walk(root, 0, 0);
                        radios = Math.Max(radios, stats.Radios);
                        cards = Math.Max(cards, stats.Cards);
                    }
                }
                catch (Exception ex) { unknown = true; errorType = ex.GetType().Name; }
            }
        }
        if (minimized > 0 && cards == 0) { unknown = true; errorType = "MinimizedContentUnavailable"; }
        Console.WriteLine(new JavaScriptSerializer().Serialize(new { Windows = windows, MinimizedWindows = minimized,
            AccessibleRoots = roots, RadioControls = radios, ChoiceCards = cards, Unknown = unknown, ErrorType = errorType }));
    }

    private static Stats Walk(IAccessible element, int childId, int depth)
    {
        if (++visited > 20000 || depth > 80) throw new InvalidOperationException("Accessibility traversal limit");
        Stats result = new Stats();
        result.Nodes = 1;
        int role = Convert.ToInt32(element.get_accRole(childId));
        if (role == 45) result.Radios = 1;
        if (role == 43)
        {
            // Only fixed action labels are compared. Never read text/option names.
            string name = element.get_accName(childId);
            result.Skip = name == "Skip" || name == "跳过";
            result.Send = name == "Send" || name == "发送" || name == "Submit";
        }
        if (childId == 0)
        {
            int count = element.accChildCount;
            if (count > 20000) throw new InvalidOperationException("Accessibility child limit");
            if (count > 0)
            {
                object[] children = new object[count];
                int obtained;
                AccessibleChildren(element, 0, count, children, out obtained);
                for (int i = 0; i < obtained; i++)
                {
                    IAccessible child = children[i] as IAccessible;
                    Stats stats = child != null ? Walk(child, 0, depth + 1) : Walk(element, Convert.ToInt32(children[i]), depth + 1);
                    result.Nodes += stats.Nodes;
                    result.Radios += stats.Radios;
                    result.Cards += stats.Cards;
                    result.Skip |= stats.Skip;
                    result.Send |= stats.Send;
                }
            }
        }
        if (result.Cards == 0 && role == 20 && result.Nodes <= 50 && result.Radios > 0 && result.Skip && result.Send) result.Cards = 1;
        return result;
    }
}
