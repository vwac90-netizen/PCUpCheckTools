using System.Runtime.InteropServices;

namespace PCUpCheckTools.Hardware;

// [Part 273] 작업 표시줄 안 요소의 화면 사각형을 UI Automation 으로 읽는다 — HwMonitorMini 는 WPF(System.Windows.Automation)를 썼다.
// WinUI 앱에 WPF 를 붙이지 않으려고 AiMeter Services/TaskbarApps.cs 와 같은 방식으로 COM IUIAutomation 을 필요한 vtable 슬롯까지만 선언한다.
// 슬롯 순서는 Windows SDK UIAutomationClient.h 그대로 — 쓰지 않는 슬롯도 자리를 채운다(어긋나면 엉뚱한 함수가 불린다, TROUBLESHOOTING Part 255).
internal static class Uia
{
    private const int TreeScopeDescendants = 4;
    private const int UIA_AutomationIdPropertyId = 30011;
    private const int UIA_ClassNamePropertyId = 30012;
    private const int UIA_BoundingRectanglePropertyId = 30001;

    /// <summary>AutomationId 로 찾은 첫 요소의 사각형 { left, top, width, height }(화면 물리 픽셀). 없거나 읽지 못하면 null.</summary>
    public static double[]? BoundsByAutomationId(IntPtr root, string automationId)
    {
        try
        {
            var automation = (IUIAutomation)new CUIAutomation();
            var element = automation.ElementFromHandle(root);
            var condition = automation.CreatePropertyCondition(UIA_AutomationIdPropertyId, automationId);
            var found = element.FindFirst(TreeScopeDescendants, condition);
            return found?.GetCurrentPropertyValue(UIA_BoundingRectanglePropertyId) is double[] r && r.Length == 4 ? r : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException)
        {
            return null;
        }
    }

    /// <summary>[Part 277] 이 클래스 이름을 가진 요소들의 오른쪽 끝 최댓값(화면 물리 픽셀). 하나도 없거나 읽지 못하면 null.
    /// 작업 표시줄 앱 버튼(Taskbar.TaskListButtonAutomationPeer) 끝을 재는 데 쓴다 — AiMeter TaskbarApps.RightEdge 와 같은 방법.</summary>
    public static int? MaxRightByClassName(IntPtr root, string className)
    {
        try
        {
            var automation = (IUIAutomation)new CUIAutomation();
            var element = automation.ElementFromHandle(root);
            var condition = automation.CreatePropertyCondition(UIA_ClassNamePropertyId, className);
            var found = element.FindAll(TreeScopeDescendants, condition);
            int? right = null;
            for (int i = 0; i < found.Length; i++)
            {
                if (found.GetElement(i).GetCurrentPropertyValue(UIA_BoundingRectanglePropertyId) is double[] r && r.Length == 4 && r[2] > 0)
                {
                    int edge = (int)Math.Ceiling(r[0] + r[2]);
                    if (right is null || edge > right) right = edge;
                }
            }
            return right;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException)
        {
            return null;
        }
    }

    [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
    private class CUIAutomation
    {
    }

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements();             // 0
        void CompareRuntimeIds();           // 1
        void GetRootElement();              // 2
        IUIAutomationElement ElementFromHandle(IntPtr hwnd); // 3
        void ElementFromPoint();            // 4
        void GetFocusedElement();           // 5
        void GetRootElementBuildCache();    // 6
        void ElementFromHandleBuildCache(); // 7
        void ElementFromPointBuildCache();  // 8
        void GetFocusedElementBuildCache(); // 9
        void CreateTreeWalker();            // 10
        void get_ControlViewWalker();       // 11
        void get_ContentViewWalker();       // 12
        void get_RawViewWalker();           // 13
        void get_RawViewCondition();        // 14
        void get_ControlViewCondition();    // 15
        void get_ContentViewCondition();    // 16
        void CreateCacheRequest();          // 17
        void CreateTrueCondition();         // 18
        void CreateFalseCondition();        // 19
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value); // 20
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();                    // 0
        void GetRuntimeId();                // 1
        IUIAutomationElement? FindFirst(int scope, [MarshalAs(UnmanagedType.IUnknown)] object condition); // 2
        IUIAutomationElementArray FindAll(int scope, [MarshalAs(UnmanagedType.IUnknown)] object condition); // 3
        void FindFirstBuildCache();         // 4
        void FindAllBuildCache();           // 5
        void BuildUpdatedCache();           // 6
        [return: MarshalAs(UnmanagedType.Struct)]
        object GetCurrentPropertyValue(int propertyId); // 7
    }

    [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElementArray
    {
        int Length { get; }                 // 0
        IUIAutomationElement GetElement(int index); // 1
    }
}
