using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace DeskCards;

/// <summary>파일·폴더를 카드로 끌어 넣기. 끌어온 것을 이 그룹 폴더로 옮긴다.</summary>
internal sealed partial class CardWindow
{
    private void EnableDrop(UIElement target)
    {
        target.AllowDrop = true;
        target.DragEnter += OnDragOver;
        target.DragOver += OnDragOver;
        target.DragLeave += (_, _) => SetDropTarget(false);
        target.Drop += OnDrop;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        // 끌어온 것이 파일·폴더인지만 먼저 보고(목록을 읽는 건 비동기), 이 폴더에 넣을 수 있는지는 놓을 때 거른다.
        bool ok = e.DataView.Contains(StandardDataFormats.StorageItems);
        e.AcceptedOperation = ok ? DataPackageOperation.Move : DataPackageOperation.None;
        SetDropTarget(ok);
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        SetDropTarget(false);
        var deferral = e.GetDeferral();
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var paths = new List<string>();
                foreach (var item in await e.DataView.GetStorageItemsAsync())
                    if (!string.IsNullOrEmpty(item.Path)) paths.Add(item.Path);
                FileOps.AddToGroup(paths, Group.Folder, Mgr.Root);
            }
            // 이동은 우리가 직접 했으므로 원본 쪽에서 삭제하지 않도록 None을 돌려준다.
            e.AcceptedOperation = DataPackageOperation.None;
        }
        finally { deferral.Complete(); }
        e.Handled = true;
    }
}
