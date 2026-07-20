using System.Windows.Controls;

namespace DynamicIsland.Sources
{
    /// <summary>
    /// MediaSource 自带的展开态视图。DataContext 由 MediaSource 自己设为 this，
    /// 所有控件直接绑 MediaSource 的属性/命令。
    /// </summary>
    public partial class MediaExpandedView : UserControl
    {
        public MediaExpandedView()
        {
            InitializeComponent();
        }
    }
}
