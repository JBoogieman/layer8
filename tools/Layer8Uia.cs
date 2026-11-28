// UI Automation for tools\Test-Layer8Console.ps1, which runs docs\REGRESSION.md's console steps: it finds the
// console's windows, reads what they show as text, and takes the few actions its allowlist names.
//
// It uses Windows' COM UI Automation API (UIAutomationCore), through the interop assembly TlbImp generates from
// UIAutomationCore.dll, and not .NET's managed System.Windows.Automation: in Windows PowerShell 5.1 the managed
// client doesn't load its Win32 proxies (registering them by hand throws a NullReferenceException), so it sees a
// WinForms ListView as an empty pane and a MessageBox's buttons as panes with nothing to press. The COM API reads
// both (probe, November 26, 2026). PowerShell can't call UI Automation's COM interfaces (they have no IDispatch), so
// this is C#, compiled by Add-Type with the .NET Framework's compiler: C# 5, no newer syntax.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Interop.UIAutomationClient;
using P = Interop.UIAutomationClient.UIA_PropertyIds;
using C = Interop.UIAutomationClient.UIA_ControlTypeIds;

namespace Layer8.Testing
{
    /// <summary>An action the allowlist doesn't name, or a keystroke refused because the wrong window is in front.</summary>
    public sealed class UiaRefusedException : Exception
    {
        public UiaRefusedException(string message) : base(message) { }
    }

    /// <summary>One element of the console's UI: what it shows, and the actions the runner may take on it.</summary>
    public sealed class UiaElement
    {
        internal readonly IUIAutomationElement Element;

        internal UiaElement(IUIAutomationElement element) { Element = element; }

        public string Name { get { return Uia.Text(Element.CurrentName); } }
        public string Type { get { return Uia.TypeName(Element.CurrentControlType); } }
        public string AutomationId { get { return Uia.Text(Element.CurrentAutomationId); } }
        public string ClassName { get { return Uia.Text(Element.CurrentClassName); } }
        public bool IsEnabled { get { return Element.CurrentIsEnabled != 0; } }
        public bool HasFocus { get { return Element.CurrentHasKeyboardFocus != 0; } }
        public int ProcessId { get { return Element.CurrentProcessId; } }
        public IntPtr Handle { get { return Element.CurrentNativeWindowHandle; } }
        public bool IsPassword { get { return Element.CurrentIsPassword != 0; } }

        /// <summary>Scrolled out of view: a Win32 header item then refuses Invoke ("not valid due to the current state").</summary>
        public bool IsOffscreen { get { return Element.CurrentIsOffscreen != 0; } }

        /// <summary>The nearest window around it (itself, if it's one): the window an allowlist entry names.</summary>
        public UiaElement Window
        {
            get
            {
                IUIAutomationElement e = Element;
                for (int i = 0; e != null && i < 80; i++)
                {
                    if (e.CurrentControlType == C.UIA_WindowControlTypeId && e.CurrentNativeWindowHandle != IntPtr.Zero) return new UiaElement(e);
                    e = Uia.Walker.GetParentElement(e);
                }
                // The walk up can dead-end: after the console was minimized and restored, its ribbon's Collection group
                // had no parent, though a walk down from the window found it (November 26, 20:49). Then the window is
                // the top-level one at the element's middle on screen. Another window covering it gives another title,
                // so an allowlist rule for this one refuses: the fallback can only make a check stricter.
                return Uia.TopWindowAt(Element);
            }
        }

        /// <summary>A text box's text. Never a password field's, and nothing in a window that may show a secret.</summary>
        public string Value
        {
            get
            {
                if (IsPassword) throw new UiaRefusedException(Describe() + " is a password field: its value isn't read");
                Uia.RefuseSecret(this);
                var value = Element.GetCurrentPattern(UIA_PatternIds.UIA_ValuePatternId) as IUIAutomationValuePattern;
                return value == null ? null : Uia.Text(value.CurrentValue);
            }
        }

        /// <summary>A check box's state: true, false, or null when it has none.</summary>
        public bool? Checked
        {
            get
            {
                var toggle = Element.GetCurrentPattern(UIA_PatternIds.UIA_TogglePatternId) as IUIAutomationTogglePattern;
                if (toggle == null) return null;
                return toggle.CurrentToggleState == ToggleState.ToggleState_On;
            }
        }

        public bool IsSelected
        {
            get
            {
                var item = Element.GetCurrentPattern(UIA_PatternIds.UIA_SelectionItemPatternId) as IUIAutomationSelectionItemPattern;
                return item != null && item.CurrentIsSelected != 0;
            }
        }

        /// <summary>What a click does, as MSAA names it ("Press", "Execute", "Open"); empty when it does nothing.</summary>
        public string DefaultAction
        {
            get
            {
                var legacy = Element.GetCurrentPattern(UIA_PatternIds.UIA_LegacyIAccessiblePatternId) as IUIAutomationLegacyIAccessiblePattern;
                return legacy == null ? string.Empty : Uia.Text(legacy.CurrentDefaultAction);
            }
        }

        public string Description
        {
            get
            {
                var legacy = Element.GetCurrentPattern(UIA_PatternIds.UIA_LegacyIAccessiblePatternId) as IUIAutomationLegacyIAccessiblePattern;
                return legacy == null ? string.Empty : Uia.Text(legacy.CurrentDescription);
            }
        }

        public bool Has(string pattern)
        {
            return Element.GetCurrentPattern(Uia.PatternId(pattern)) != null;
        }

        public override string ToString() { return Describe(); }

        public string Describe()
        {
            string name = Name;
            return Type + (name.Length > 0 ? " '" + (name.Length > 80 ? name.Substring(0, 80) + "..." : name) + "'" : string.Empty);
        }

        public UiaElement[] Children()
        {
            var list = new List<UiaElement>();
            IUIAutomationElement c = Uia.Walker.GetFirstChildElement(Element);
            while (c != null) { list.Add(new UiaElement(c)); c = Uia.Walker.GetNextSiblingElement(c); }
            return list.ToArray();
        }

        /// <summary>
        /// The first element below this one of the given type ("Button", "MenuItem"...; null or empty for any) whose
        /// name is exactly <paramref name="name"/> (null or empty for any), waiting up to <paramref name="timeoutMs"/> for it.
        /// </summary>
        public UiaElement Find(string type, string name, int timeoutMs)
        {
            IUIAutomationCondition condition = Uia.Condition(type, name);
            return Uia.Poll(timeoutMs, delegate
            {
                IUIAutomationElement e = Element.FindFirst(TreeScope.TreeScope_Descendants, condition);
                return e == null ? null : new UiaElement(e);
            });
        }

        /// <summary>The same, with the name matched by a regular expression.</summary>
        public UiaElement FindLike(string type, string namePattern, int timeoutMs)
        {
            IUIAutomationCondition condition = Uia.Condition(type, null);
            var regex = new Regex(namePattern);
            return Uia.Poll(timeoutMs, delegate
            {
                IUIAutomationElementArray all = Element.FindAll(TreeScope.TreeScope_Descendants, condition);
                for (int i = 0; i < all.Length; i++)
                {
                    IUIAutomationElement e = all.GetElement(i);
                    if (regex.IsMatch(Uia.Text(e.CurrentName))) return new UiaElement(e);
                }
                return null;
            });
        }

        public UiaElement[] FindAll(string type, string name)
        {
            IUIAutomationElementArray all = Element.FindAll(TreeScope.TreeScope_Descendants, Uia.Condition(type, name));
            var list = new List<UiaElement>();
            for (int i = 0; i < all.Length; i++) list.Add(new UiaElement(all.GetElement(i)));
            return list.ToArray();
        }

        /// <summary>A grid's column headers, in order, as they read ("Device" followed by its sort arrow, say).</summary>
        public string[] Columns()
        {
            return FindAll("HeaderItem", null).Select(each => each.Name).ToArray();
        }

        /// <summary>
        /// A grid's rows (a WinForms ListView or the console's WPF DataGrid), each as its cells' texts in column
        /// order, read in one round trip. Cells scrolled out of view are read too.
        /// </summary>
        public string[][] Rows()
        {
            Uia.RefuseSecret(this);
            IUIAutomationElement grid = Element.BuildUpdatedCache(Uia.RowCache);
            var rows = new List<string[]>();
            IUIAutomationElementArray kids = grid.GetCachedChildren();
            if (kids == null) return rows.ToArray();
            for (int i = 0; i < kids.Length; i++)
            {
                IUIAutomationElement row = kids.GetElement(i);
                int type = row.CachedControlType;
                if (type != C.UIA_ListItemControlTypeId && type != C.UIA_DataItemControlTypeId) continue;
                var cells = new SortedDictionary<int, string>();
                IUIAutomationElementArray parts = row.GetCachedChildren();
                if (parts != null)
                {
                    for (int j = 0; j < parts.Length; j++)
                    {
                        IUIAutomationElement cell = parts.GetElement(j);
                        object column = cell.GetCachedPropertyValue(P.UIA_GridItemColumnPropertyId);
                        if (column is int && !cells.ContainsKey((int)column)) cells[(int)column] = Uia.Text(cell.CachedName);
                    }
                }
                int width = cells.Count == 0 ? 0 : cells.Keys.Max() + 1;
                var texts = new string[width];
                for (int c = 0; c < width; c++) texts[c] = cells.ContainsKey(c) ? cells[c] : string.Empty;
                rows.Add(texts);
            }
            return rows.ToArray();
        }

        /// <summary>The element and everything below it as indented text, for a report's evidence.</summary>
        public string Dump(int depth)
        {
            Uia.RefuseSecret(this);
            IUIAutomationElement root = Element.BuildUpdatedCache(Uia.DumpCache);
            var sb = new StringBuilder();
            Uia.DumpCached(root, 0, depth, sb);
            return sb.ToString();
        }

        // --- Actions: each one checked against the allowlist first. ---------------------------------------------

        public void Invoke()
        {
            Uia.Check(this, "Invoke");
            var invoke = Element.GetCurrentPattern(UIA_PatternIds.UIA_InvokePatternId) as IUIAutomationInvokePattern;
            if (invoke == null) throw new InvalidOperationException(Describe() + " can't be invoked");
            invoke.Invoke();
        }

        /// <summary>
        /// Invoke without waiting for the click to finish. A WinForms button or menu item runs its click inside the
        /// Invoke call, so one that opens a modal window (a MessageBox, the Move to OU list) doesn't return until that
        /// window closes. The console's WPF ribbon and menus don't: WPF runs the click later. So the Invoke runs on a
        /// thread of its own, through a UI Automation connection of its own, the element found there again by its
        /// runtime ID; this returns once that's done, or after <paramref name="waitMs"/> with the window still open.
        /// The allowlist is checked here, first, as for Invoke. Until that window closes, UI Automation can't read
        /// the console from any thread (the Fix button's message was open and unseen for 5 seconds, November 27,
        /// 08:25), so this is only for a window a person answers; a key (Uia.Key) leaves the console readable.
        /// </summary>
        public void InvokeNoWait(int waitMs)
        {
            Uia.Check(this, "Invoke");
            if (Element.GetCurrentPattern(UIA_PatternIds.UIA_InvokePatternId) == null) throw new InvalidOperationException(Describe() + " can't be invoked");
            int[] runtimeId = (int[])Element.GetRuntimeId();
            UiaElement window = Window;
            IntPtr root = window == null ? IntPtr.Zero : window.Handle;
            if (root == IntPtr.Zero) throw new InvalidOperationException(Describe() + " has no window to find it in again");
            string described = Describe();
            Exception failed = null;
            var thread = new Thread(delegate()
            {
                try
                {
                    var automation = new CUIAutomation8Class();
                    IUIAutomationElement top = automation.ElementFromHandle(root);
                    IUIAutomationElement same = top.FindFirst(TreeScope.TreeScope_Subtree, automation.CreatePropertyCondition(P.UIA_RuntimeIdPropertyId, runtimeId));
                    if (same == null) throw new InvalidOperationException(described + " wasn't there to invoke any more");
                    var invoke = same.GetCurrentPattern(UIA_PatternIds.UIA_InvokePatternId) as IUIAutomationInvokePattern;
                    if (invoke == null) throw new InvalidOperationException(described + " can't be invoked");
                    invoke.Invoke();
                }
                catch (Exception e) { failed = e; }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            // A timeout only means the click is still running (its window is open); anything else is a real failure.
            if (thread.Join(waitMs) && failed != null && !(failed is TimeoutException)) throw new InvalidOperationException(described + ": " + failed.Message, failed);
        }

        public void Select()
        {
            Uia.Check(this, "Select");
            var item = Element.GetCurrentPattern(UIA_PatternIds.UIA_SelectionItemPatternId) as IUIAutomationSelectionItemPattern;
            if (item == null) throw new InvalidOperationException(Describe() + " can't be selected");
            item.Select();
        }

        /// <summary>Adds the item to a multi-select list's selection (the allowlist's Select).</summary>
        public void AddToSelection()
        {
            Uia.Check(this, "Select");
            var item = Element.GetCurrentPattern(UIA_PatternIds.UIA_SelectionItemPatternId) as IUIAutomationSelectionItemPattern;
            if (item == null) throw new InvalidOperationException(Describe() + " can't be selected");
            item.AddToSelection();
        }

        public void Expand()
        {
            Uia.Check(this, "Expand");
            var expand = Element.GetCurrentPattern(UIA_PatternIds.UIA_ExpandCollapsePatternId) as IUIAutomationExpandCollapsePattern;
            if (expand == null) throw new InvalidOperationException(Describe() + " can't be expanded");
            expand.Expand();
        }

        public void Collapse()
        {
            Uia.Check(this, "Collapse");
            var expand = Element.GetCurrentPattern(UIA_PatternIds.UIA_ExpandCollapsePatternId) as IUIAutomationExpandCollapsePattern;
            if (expand == null) throw new InvalidOperationException(Describe() + " can't be collapsed");
            expand.Collapse();
        }

        public void Toggle()
        {
            Uia.Check(this, "Toggle");
            var toggle = Element.GetCurrentPattern(UIA_PatternIds.UIA_TogglePatternId) as IUIAutomationTogglePattern;
            if (toggle == null) throw new InvalidOperationException(Describe() + " can't be toggled");
            toggle.Toggle();
        }

        public void SetValue(string value)
        {
            Uia.Check(this, "SetValue");
            if (IsPassword) throw new UiaRefusedException(Describe() + " is a password field");
            var pattern = Element.GetCurrentPattern(UIA_PatternIds.UIA_ValuePatternId) as IUIAutomationValuePattern;
            if (pattern == null) throw new InvalidOperationException(Describe() + " has no value to set");
            pattern.SetValue(value);
        }

        public void Focus()
        {
            Uia.Check(this, "Focus");
            Element.SetFocus();
        }

        public bool SameAs(UiaElement other)
        {
            return other != null && Uia.Automation.CompareElements(Element, other.Element) != 0;
        }
    }

    /// <summary>The runner's connection to one console process, its allowlist, and the guarded keystrokes.</summary>
    public static class Uia
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        // INPUT's union is as large as MOUSEINPUT, the largest member; padding keeps the struct that size on 32 and 64 bit.
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public KEYBDINPUT ki; public uint pad1; public uint pad2; }

        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        private const uint InputKeyboard = 1;
        private const uint KeyUp = 0x0002;

        internal static CUIAutomation8Class Automation;
        internal static IUIAutomationTreeWalker Walker;
        internal static IUIAutomationCacheRequest RowCache;
        internal static IUIAutomationCacheRequest DumpCache;
        private static readonly Dictionary<string, int> typeIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<int, string> typeNames = new Dictionary<int, string>();
        private static readonly List<Rule> rules = new List<Rule>();

        /// <summary>A window this runner never reads: their text can be a LAPS password or a BitLocker recovery key.</summary>
        public static readonly Regex Secret = new Regex("LAPS|BitLocker|Recovery|Password", RegexOptions.IgnoreCase);

        public static int ProcessId { get; private set; }
        public static IntPtr MainHandle { get; private set; }

        private sealed class Rule
        {
            public Regex Window; public string Type; public Regex Name; public string Actions;
            public override string ToString() { return Actions + " " + Type + " /" + Name + "/ in /" + Window + "/"; }
        }

        /// <summary>Connects to the console process: everything after this acts only inside it.</summary>
        public static UiaElement Attach(int processId, IntPtr mainHandle)
        {
            Automation = new CUIAutomation8Class();
            Walker = Automation.ControlViewWalker;
            ProcessId = processId;
            MainHandle = mainHandle;
            foreach (var field in typeof(UIA_ControlTypeIds).GetFields())
            {
                string name = field.Name.Replace("UIA_", string.Empty).Replace("ControlTypeId", string.Empty);
                int id = (int)field.GetValue(null);
                typeIds[name] = id;
                typeNames[id] = name;
            }
            RowCache = Automation.CreateCacheRequest();
            RowCache.AddProperty(P.UIA_ControlTypePropertyId);
            RowCache.AddProperty(P.UIA_NamePropertyId);
            RowCache.AddProperty(P.UIA_GridItemColumnPropertyId);
            RowCache.TreeScope = TreeScope.TreeScope_Subtree;
            RowCache.TreeFilter = Automation.ControlViewCondition;
            RowCache.AutomationElementMode = AutomationElementMode.AutomationElementMode_None;
            DumpCache = Automation.CreateCacheRequest();
            foreach (int id in new[] { P.UIA_ControlTypePropertyId, P.UIA_NamePropertyId, P.UIA_AutomationIdPropertyId,
                P.UIA_IsEnabledPropertyId, P.UIA_IsPasswordPropertyId, P.UIA_ValueValuePropertyId, P.UIA_HasKeyboardFocusPropertyId })
                DumpCache.AddProperty(id);
            DumpCache.TreeScope = TreeScope.TreeScope_Subtree;
            DumpCache.TreeFilter = Automation.ControlViewCondition;
            DumpCache.AutomationElementMode = AutomationElementMode.AutomationElementMode_None;
            return new UiaElement(Automation.ElementFromHandle(mainHandle));
        }

        /// <summary>
        /// Allows <paramref name="actions"/> (comma-separated: Invoke, Select, Expand, Collapse, Toggle, SetValue,
        /// Focus, Key) on elements of <paramref name="type"/> whose name matches <paramref name="namePattern"/>, in a
        /// window whose title matches <paramref name="windowPattern"/>. Patterns are anchored: they match the whole name.
        /// Singleline, so . matches a line break too: the console's result rows have names that span lines.
        /// </summary>
        public static void Allow(string windowPattern, string type, string namePattern, string actions)
        {
            rules.Add(new Rule {
                Window = new Regex("^(?:" + windowPattern + ")$", RegexOptions.Singleline),
                Type = type,
                Name = new Regex("^(?:" + namePattern + ")$", RegexOptions.Singleline),
                Actions = actions });
        }

        private sealed class Denial { public Regex Window; public string Type; public Regex Name; public string WindowHas; public string Why; }
        private static readonly List<Denial> denials = new List<Denial>();

        /// <summary>
        /// Refuses any action on elements of <paramref name="type"/> named like <paramref name="namePattern"/> in a
        /// window titled like <paramref name="windowPattern"/>, whatever the allowlist says; with
        /// <paramref name="windowHas"/>, only in such a window that holds an element of that type (a count box's OK,
        /// not a summary's). For what a person must always do by hand, such as the Yes that changes Active Directory.
        /// </summary>
        public static void Deny(string windowPattern, string type, string namePattern, string windowHas, string why)
        {
            denials.Add(new Denial {
                Window = new Regex("^(?:" + windowPattern + ")$", RegexOptions.Singleline),
                Type = type,
                Name = new Regex("^(?:" + namePattern + ")$", RegexOptions.Singleline),
                WindowHas = windowHas,
                Why = why });
        }

        internal static void Check(UiaElement element, string action)
        {
            if (element.ProcessId != ProcessId) throw new UiaRefusedException(action + " on " + element.Describe() + ": not the console's");
            UiaElement window = element.Window;
            string title = window == null ? string.Empty : window.Name;
            if (Secret.IsMatch(title)) throw new UiaRefusedException(action + " on " + element.Describe() + ": in '" + title + "', which may show a secret");
            string type = element.Type;
            string name = element.Name;
            foreach (Denial denial in denials)
            {
                if (!string.Equals(denial.Type, type, StringComparison.OrdinalIgnoreCase) || !denial.Name.IsMatch(name) || !denial.Window.IsMatch(title)) continue;
                if (!string.IsNullOrEmpty(denial.WindowHas) && (window == null || window.Find(denial.WindowHas, null, 0) == null)) continue;
                throw new UiaRefusedException(action + " on " + element.Describe() + " in '" + title + "': " + denial.Why);
            }
            foreach (Rule rule in rules)
            {
                if (!rule.Actions.Split(',').Any(each => string.Equals(each.Trim(), action, StringComparison.OrdinalIgnoreCase))) continue;
                if (!string.Equals(rule.Type, type, StringComparison.OrdinalIgnoreCase)) continue;
                if (rule.Name.IsMatch(name) && rule.Window.IsMatch(title)) return;
            }
            throw new UiaRefusedException(action + " on " + element.Describe() + " in '" + title + "': not on the allowlist");
        }

        /// <summary>Refuses to read inside a window whose title may show a secret.</summary>
        internal static void RefuseSecret(UiaElement element)
        {
            UiaElement window = element.Window;
            if (window != null && Secret.IsMatch(window.Name)) throw new UiaRefusedException("'" + window.Name + "' isn't read: it may show a secret");
        }

        /// <summary>
        /// The console's windows, owned ones included: UI Automation shows a dialog, a MessageBox or a menu under the
        /// window that owns it, not under the desktop. Windows that may show a secret are left out.
        /// </summary>
        public static UiaElement[] Windows()
        {
            var found = new List<UiaElement>();
            var seen = new HashSet<string>();
            IUIAutomationCondition byProcess = Automation.CreatePropertyCondition(P.UIA_ProcessIdPropertyId, ProcessId);
            IUIAutomationCondition owned = Automation.CreateOrConditionFromArray(new[] {
                Automation.CreatePropertyCondition(P.UIA_ControlTypePropertyId, C.UIA_WindowControlTypeId),
                Automation.CreatePropertyCondition(P.UIA_ControlTypePropertyId, C.UIA_MenuControlTypeId),
                Automation.CreatePropertyCondition(P.UIA_ControlTypePropertyId, C.UIA_ToolBarControlTypeId) });
            var queue = new Queue<IUIAutomationElement>();
            IUIAutomationElementArray tops;
            // UIA_E_TIMEOUT (0x80131505) while a click waits on a window it opened (see InvokeNoWait): none found yet.
            // It comes as a COMException (Move to OU's list, November 27, 08:34), and as a TimeoutException elsewhere.
            try { tops = Automation.GetRootElement().FindAll(TreeScope.TreeScope_Children, byProcess); }
            catch (TimeoutException) { return found.ToArray(); }
            catch (COMException) { return found.ToArray(); }
            for (int i = 0; i < tops.Length; i++) queue.Enqueue(tops.GetElement(i));
            while (queue.Count > 0)
            {
                IUIAutomationElement w = queue.Dequeue();
                try
                {
                    if (!seen.Add(string.Join(".", ((int[])w.GetRuntimeId()).Select(each => each.ToString())))) continue;
                    if (Text(w.CurrentClassName) == "SysShadow") continue;
                    if (w.CurrentControlType == C.UIA_WindowControlTypeId && Secret.IsMatch(Text(w.CurrentName))) continue;
                    found.Add(new UiaElement(w));
                    IUIAutomationElementArray kids = w.FindAll(TreeScope.TreeScope_Children, owned);
                    for (int i = 0; i < kids.Length; i++) queue.Enqueue(kids.GetElement(i));
                }
                catch (COMException) { }
                catch (TimeoutException) { }
            }
            return found.ToArray();
        }

        /// <summary>The first of the console's windows (not the main one) of a type whose title matches, waiting up to the timeout.</summary>
        public static UiaElement WaitWindow(string type, string titlePattern, int timeoutMs)
        {
            var regex = new Regex("^(?:" + titlePattern + ")$");
            return Poll(timeoutMs, delegate
            {
                foreach (UiaElement w in Windows())
                {
                    if (w.Handle == MainHandle) continue;
                    if ((string.IsNullOrEmpty(type) || string.Equals(w.Type, type, StringComparison.OrdinalIgnoreCase)) && regex.IsMatch(w.Name)) return w;
                }
                return null;
            });
        }

        /// <summary>
        /// A menu item showing in an open menu or drop-down of the console (not searched for inside the main window,
        /// where a full search takes seconds), waiting up to the timeout.
        /// </summary>
        public static UiaElement WaitMenuItem(string name, int timeoutMs)
        {
            IUIAutomationCondition condition = Condition("MenuItem", name);
            return Poll(timeoutMs, delegate
            {
                foreach (UiaElement w in Windows())
                {
                    if (w.Handle == MainHandle) continue;
                    IUIAutomationElement e = w.Element.FindFirst(TreeScope.TreeScope_Descendants, condition);
                    if (e != null) return new UiaElement(e);
                }
                return null;
            });
        }

        public static UiaElement Focused()
        {
            IUIAutomationElement e = Automation.GetFocusedElement();
            return e == null ? null : new UiaElement(e);
        }

        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);

        /// <summary>
        /// Whether a window is still there, by its handle. A closed MessageBox's UI Automation element goes on
        /// answering, with an empty name and no error, so its element can't tell (the Move to OU question
        /// answered at 08:57 looked open for 10 more minutes, November 27).
        /// </summary>
        public static bool IsOpen(IntPtr handle) { return handle != IntPtr.Zero && IsWindow(handle); }
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }

        private const uint AncestorRoot = 2;

        internal static UiaElement TopWindowAt(IUIAutomationElement element)
        {
            try
            {
                tagRECT r = element.CurrentBoundingRectangle;
                if (r.right <= r.left || r.bottom <= r.top) return null;
                IntPtr top = GetAncestor(WindowFromPoint(new POINT { X = (r.left + r.right) / 2, Y = (r.top + r.bottom) / 2 }), AncestorRoot);
                if (top == IntPtr.Zero) return null;
                IUIAutomationElement window = Automation.ElementFromHandle(top);
                return window.CurrentProcessId == element.CurrentProcessId ? new UiaElement(window) : null;
            }
            catch (COMException) { return null; }
        }

        /// <summary>
        /// True when the window in front belongs to the console's process and the console isn't minimized: a minimized
        /// console can still be the window in front, but its grid then refuses the focus and its ribbon lays out
        /// differently (November 26, 20:46).
        /// </summary>
        public static bool ConsoleInFront()
        {
            uint process;
            GetWindowThreadProcessId(GetForegroundWindow(), out process);
            return process == (uint)ProcessId && !IsIconic(MainHandle);
        }

        /// <summary>
        /// Brings the console's main window to the front through UI Automation, restoring it first if it's minimized
        /// (as clicking it on the taskbar would); true if it's in front afterwards.
        /// </summary>
        public static bool BringToFront()
        {
            try
            {
                IUIAutomationElement window = Automation.ElementFromHandle(MainHandle);
                var state = window.GetCurrentPattern(UIA_PatternIds.UIA_WindowPatternId) as IUIAutomationWindowPattern;
                if (state != null && state.CurrentWindowVisualState == WindowVisualState.WindowVisualState_Minimized)
                {
                    state.SetWindowVisualState(WindowVisualState.WindowVisualState_Normal);
                    Thread.Sleep(700);
                }
                window.SetFocus();
            }
            catch (COMException) { }
            Thread.Sleep(300);
            return ConsoleInFront();
        }

        /// <summary>
        /// Presses a key combination ("Shift+F10", "Space", "Escape"), only while a window of the console is in front
        /// and, when <paramref name="mustHaveFocus"/> is given, while that element has the keyboard focus. Keystrokes
        /// go to whatever window is in front, so without these checks they could land in another program.
        /// </summary>
        public static void Key(string combo, UiaElement mustHaveFocus)
        {
            if (!rules.Any(each => string.Equals(each.Type, "Key", StringComparison.OrdinalIgnoreCase) && each.Name.IsMatch(combo)))
                throw new UiaRefusedException("Key " + combo + ": not on the allowlist");
            if (!ConsoleInFront()) throw new UiaRefusedException("Key " + combo + ": the console isn't the window in front");
            if (mustHaveFocus != null)
            {
                UiaElement focused = Focused();
                if (focused == null || !focused.SameAs(mustHaveFocus))
                    throw new UiaRefusedException("Key " + combo + ": " + mustHaveFocus.Describe() + " doesn't have the focus (" + (focused == null ? "nothing" : focused.Describe()) + " has)");
            }
            var keys = combo.Split('+').Select(VirtualKey).ToArray();
            var inputs = new List<INPUT>();
            foreach (ushort k in keys) inputs.Add(new INPUT { type = InputKeyboard, ki = new KEYBDINPUT { wVk = k } });
            foreach (ushort k in keys.Reverse()) inputs.Add(new INPUT { type = InputKeyboard, ki = new KEYBDINPUT { wVk = k, dwFlags = KeyUp } });
            uint sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
            if (sent != inputs.Count) throw new InvalidOperationException("Key " + combo + ": Windows took " + sent + " of " + inputs.Count + " key events (error " + Marshal.GetLastWin32Error() + ")");
        }

        private static ushort VirtualKey(string name)
        {
            switch (name.Trim().ToLowerInvariant())
            {
                case "shift": return 0x10;
                case "ctrl": return 0x11;
                case "alt": return 0x12;
                case "escape": return 0x1B;
                case "space": return 0x20;
                case "enter": return 0x0D;
                case "tab": return 0x09;
                case "apps": return 0x5D;
                case "f10": return 0x79;
                default: throw new ArgumentException("No key named " + name);
            }
        }

        // --- Helpers ------------------------------------------------------------------------------------------------

        internal static string Text(string s) { return s ?? string.Empty; }

        internal static string TypeName(int id)
        {
            string name;
            return typeNames.TryGetValue(id, out name) ? name : id.ToString();
        }

        internal static int PatternId(string name)
        {
            var field = typeof(UIA_PatternIds).GetField("UIA_" + name + "PatternId");
            if (field == null) throw new ArgumentException("No pattern named " + name);
            return (int)field.GetValue(null);
        }

        /// <summary>
        /// A type and an exact name, either one null or empty for any. Empty counts as any because PowerShell passes
        /// $null to a string parameter as "" (a search for rows named "" found none, November 26, 2026).
        /// </summary>
        internal static IUIAutomationCondition Condition(string type, string name)
        {
            var parts = new List<IUIAutomationCondition>();
            if (!string.IsNullOrEmpty(type))
            {
                int id;
                if (!typeIds.TryGetValue(type, out id)) throw new ArgumentException("No control type named " + type);
                parts.Add(Automation.CreatePropertyCondition(P.UIA_ControlTypePropertyId, id));
            }
            if (!string.IsNullOrEmpty(name)) parts.Add(Automation.CreatePropertyCondition(P.UIA_NamePropertyId, name));
            if (parts.Count == 0) return Automation.CreateTrueCondition();
            if (parts.Count == 1) return parts[0];
            return Automation.CreateAndConditionFromArray(parts.ToArray());
        }

        internal static UiaElement Poll(int timeoutMs, Func<UiaElement> attempt)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    UiaElement found = attempt();
                    if (found != null) return found;
                }
                catch (COMException) { }   // a window closing while it's searched
                catch (TimeoutException) { }   // the console busy for a moment (UIA_E_TIMEOUT): not found yet
                if (watch.ElapsedMilliseconds >= timeoutMs) return null;
                Thread.Sleep(200);
            }
        }

        internal static void DumpCached(IUIAutomationElement e, int level, int depth, StringBuilder sb)
        {
            object typeValue = e.GetCachedPropertyValue(P.UIA_ControlTypePropertyId);
            string type = typeValue is int ? TypeName((int)typeValue) : "?";
            string name = Text(e.GetCachedPropertyValue(P.UIA_NamePropertyId) as string);
            if (level > 0 && type == "Window" && Secret.IsMatch(name)) { sb.Append(' ', level * 2).AppendLine("(window not read: it may show a secret)"); return; }
            sb.Append(' ', level * 2).Append(type);
            if (name.Length > 0) sb.Append(" '").Append(name.Replace("\r", string.Empty).Replace("\n", " ")).Append('\'');
            string id = e.GetCachedPropertyValue(P.UIA_AutomationIdPropertyId) as string;
            if (!string.IsNullOrEmpty(id)) sb.Append(" id=").Append(id);
            object password = e.GetCachedPropertyValue(P.UIA_IsPasswordPropertyId);
            string value = e.GetCachedPropertyValue(P.UIA_ValueValuePropertyId) as string;
            if (password is bool && (bool)password) sb.Append(" (password field)");
            else if (!string.IsNullOrEmpty(value)) sb.Append(" value='").Append(value).Append('\'');
            object enabled = e.GetCachedPropertyValue(P.UIA_IsEnabledPropertyId);
            if (enabled is bool && !(bool)enabled) sb.Append(" disabled");
            object focus = e.GetCachedPropertyValue(P.UIA_HasKeyboardFocusPropertyId);
            if (focus is bool && (bool)focus) sb.Append(" focused");
            sb.AppendLine();
            if (level >= depth) return;
            IUIAutomationElementArray kids = e.GetCachedChildren();
            if (kids == null) return;
            for (int i = 0; i < kids.Length; i++) DumpCached(kids.GetElement(i), level + 1, depth, sb);
        }
    }
}
