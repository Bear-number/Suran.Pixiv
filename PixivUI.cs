using Alife.Framework;
using Microsoft.AspNetCore.Components.Rendering;

namespace Suran.Pixiv;

/// <summary>P站图片搜索发送 的设置页。内容由 SuranUiKit 按配置项自动分组生成。</summary>
public sealed class PixivUI : ModuleUIBase<PixivModule, PixivConfig>
{
    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        if (Configuration == null)
        {
            b.AddContent(0, "配置未加载");
            return;
        }
        SuranUiKit.Render(this, StateHasChanged, b, Configuration, "P站图片搜索发送", "免登录搜 Pixiv 原图并发出；带排除标签、收藏门槛、去重与缓存清理", "#0096fa");
    }
}
