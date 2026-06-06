using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_TakeScreenshot : ITool, IHasAvailability
    {
        public bool IsAvailable => McpOssConfig.IsConfigured;

        public string Name => "take_screenshot";
        public string Description => "截取地图指定区域的画面，自动上传 OSS 并返回公网 URL。需先在 Mod 设置中配置 OSS。坐标范围为闭区间（两端坐标均包含）。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pos_x = new { type = "integer", description = "截图范围最小 X 坐标" },
                end_x = new { type = "integer", description = "截图范围最大 X 坐标（含）" },
                pos_y = new { type = "integer", description = "截图范围最小 Y 坐标（可选）" },
                end_y = new { type = "integer", description = "截图范围最大 Y 坐标（可选）" },
                file_name = new { type = "string", description = "输出文件名，不含扩展名（可选）" }
            },
            required = new[] { "pos_x", "end_x" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("pos_x", out var jMinX) || !jMinX.TryGetInt32(out var minX))
                return ToolResult.Error("缺少必填参数: pos_x");
            if (!args.Value.TryGetProperty("end_x", out var jMaxX) || !jMaxX.TryGetInt32(out var maxX))
                return ToolResult.Error("缺少必填参数: end_x");

            int? minZ = null, maxZ = null;
            if (args.Value.TryGetProperty("pos_y", out var jMinZ) && jMinZ.TryGetInt32(out var mz)) minZ = mz;
            if (args.Value.TryGetProperty("end_y", out var jMaxZ) && jMaxZ.TryGetInt32(out var mz2)) maxZ = mz2;

            string fileName = "";
            if (args.Value.TryGetProperty("file_name", out var jFileName))
                fileName = jFileName.GetString() ?? "";

            if (minX > maxX) return ToolResult.Error($"min_x ({minX}) 不能大于 max_x ({maxX})");
            if (minZ.HasValue && maxZ.HasValue && minZ.Value > maxZ.Value)
                return ToolResult.Error($"min_z ({minZ}) 不能大于 max_z ({maxZ})");

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图，请先加载游戏存档。");

                    if (minX < 0 || maxX >= map.Size.x)
                        return ToolResult.Error($"X 范围 ({minX}~{maxX}) 超出地图边界");
                    if (minZ.HasValue && (minZ.Value < 0 || maxZ!.Value >= map.Size.z))
                        return ToolResult.Error($"Z 范围 ({minZ}~{maxZ}) 超出地图边界");

                    var driver = Find.CameraDriver;
                    CellRect oldView = driver.CurrentViewRect;
                    float oldRootSize = driver.RootSize;

                    int viewWidth = maxX - minX + 1;
                    float aspectRatio = (float)UI.screenWidth / (float)UI.screenHeight;
                    float rootSize = (viewWidth + 4) / (2f * aspectRatio);
                    float centerX = (minX + maxX) / 2f;
                    float centerZ;

                    if (minZ.HasValue && maxZ.HasValue)
                    {
                        int viewHeight = maxZ.Value - minZ.Value + 1;
                        rootSize = Math.Max(rootSize, (viewHeight + 4) / 2f);
                        centerZ = (minZ.Value + maxZ.Value) / 2f;
                    }
                    else
                    {
                        centerZ = (oldView.minZ + oldView.maxZ) / 2f;
                    }

                    driver.SetRootPosAndSize(new Vector3(centerX, 0f, centerZ), rootSize);
                    Find.UIRoot.screenshotMode.Active = true;

                    // 本地文件始终用 ASCII 安全名（Unity CaptureScreenshot 不支持 Unicode 文件名）
                    string localFile = $"mcp_{DateTime.Now:yyyyMMdd_HHmmss}";
                    string objectKey = (!string.IsNullOrEmpty(fileName) ? fileName : localFile) + ".png";

                    if (!McpOssConfig.IsConfigured)
                        return ToolResult.Error("OSS 未配置，请在游戏 Mod 设置中配置 OSS 后再使用截图功能。");

                    ScreenshotTaker.TakeNonSteamShot(localFile);
                    // screenshotMode 和相机必须在帧末 Unity CaptureScreenshot 完成后再恢复
                    McpCommandQueue.ScheduleDeferred(() =>
                    {
                        Find.UIRoot.screenshotMode.Active = false;
                        driver.SetRootPosAndSize(new Vector3(oldView.CenterCell.x, 0f, oldView.CenterCell.z),
                            oldRootSize);
                    });

                    string fullPath = Path.GetFullPath(Path.Combine(GenFilePaths.ScreenshotFolderPath, localFile + ".png"));
                    McpOssUploader.EnqueuePendingUpload(fullPath, objectKey);
                    string publicUrl = McpOssUploader.GetPublicUrl(objectKey);

                    return ToolResult.Success(
                        $"已截取地图区域 ({minX}~{maxX}, {minZ}~{maxZ})。\n" +
                        $"- 截图将在帧末保存并自动上传 OSS\n" +
                        $"- OSS URL: {publicUrl}");
                }
                catch (Exception ex) { return ToolResult.Error($"截图失败: {ex.Message}"); }
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX)) return null;
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY)) return null;
            if (args.Value.TryGetProperty("end_x", out var jEX) && jEX.TryGetInt32(out var endX)
                && args.Value.TryGetProperty("end_y", out var jEY) && jEY.TryGetInt32(out var endY))
                return (Math.Min(posX, endX), Math.Min(posY, endY), Math.Max(posX, endX), Math.Max(posY, endY));
            return (posX, posY, posX, posY);
        }
    }
}
