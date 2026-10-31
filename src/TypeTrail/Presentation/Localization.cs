using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
namespace TypeTrail {
    internal static class L {
        public static string Language = "auto";
        public static bool English { get { return Language == "en" || (Language == "auto" && !CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)); } }
        public static readonly Dictionary<string, string> Strings = new Dictionary<string, string> {
            { "拾字", "TypeTrail" },
            { "历史尚未保存，已取消退出。请检查数据目录后重试。", "History has not been saved. TypeTrail is still running; check the data folder and try exiting again." },
            { "拾字 · 输入历史", "TypeTrail · Input history" },
            { "拾字 · ", "TypeTrail · " },
            { "打过的字，随时找回", "Your words, within reach." },
            { "●  正在记录", "●  Recording" },
            { "暂停或恢复记录", "Pause or resume recording" },
            { "设置", "Settings" },
            { "切换到深色模式", "Switch to dark mode" },
            { "切换到浅色模式", "Switch to light mode" },
            { "输入监听暂不可用（", "Input capture is temporarily unavailable (" },
            { "当前应用的输入线程暂不可访问", "The current app's input thread is temporarily inaccessible" },
            { "输入窗口响应较慢，部分末尾文字尚未确认", "The input window is slow; some trailing text has not been confirmed" },
            { "收起到后台 · Esc", "Hide to background · Esc" },
            { "搜索文字或应用…", "Search words or apps…" },
            { "最近输入", "Recent input" },
            { "历史暂时无法读取，原文件已保护，正在重试。", "History is temporarily unavailable. The original file is protected and will be retried." },
            { "正在恢复历史…", "Restoring history…" },
            { "原历史文件已保护，新输入会暂存在内存，恢复后一起显示。", "The original history is protected. New input stays in memory and will appear together after recovery." },
            { "历史已恢复", "History restored" },
            { "设置暂时无法读取，原文件未改动，请稍后重试。", "Settings are temporarily unavailable. The original file is unchanged; please retry shortly." },
            { "搜索结果", "Search results" },
            { "删除", "Delete" },
            { "同一段文字的修改前内容，集中保存在这里", "Earlier versions of this entry" },
            { "拖选文字，可只复制选中部分", "Select text to copy just a part" },
            { "复制选中", "Copy selection" },
            { "复制整段", "Copy all" },
            { "下一次灵感，不用重打一遍", "Keep your next thought." },
            { "去任意支持的输入框打字，历史会自动出现在这里。", "Type in a supported app. Find your words here." },
            { "Alt + `    随时呼出", "Alt + `    Open anywhere" },
            { "本机保存  ·  自动滚动清理  ·  标准密码框不记录", "Stored locally  ·  Oldest entries clear automatically  ·  Password fields skipped" },
            { "就绪", "Ready" },
            { "按你的习惯拾字", "Make TypeTrail yours" },
            { "呼出快捷键", "Open history" },
            { "点击后直接按新的组合键；Esc 取消。", "Click, then press a shortcut. Esc to cancel." },
            { "最多保留字符", "History capacity" },
            { "10,000 字符", "10,000 characters" },
            { "50,000 字符", "50,000 characters" },
            { "100,000 字符", "100,000 characters" },
            { "500,000 字符", "500,000 characters" },
            { "达到上限后，自动移除最早的记录。", "The oldest entries clear when the limit is reached." },
            { "退出后保留历史（使用当前 Windows 账户加密）", "Keep history after quitting (encrypted for this Windows account)" },
            { "登录 Windows 后自动在后台运行", "Launch in the background when I sign in to Windows" },
            { "不记录这些应用", "Excluded apps" },
            { "填写进程名，用逗号分隔，例如 WeChat, chrome。标准密码框自动跳过。", "Process names, separated by commas. For example: WeChat, chrome. Password fields are skipped." },
            { "打过的字自动保存，按快捷键随时找回。同一输入框连续记录，中英文混输合并；原有正文不会整段存入历史。", "Find typed words with a shortcut. Continuous input stays together, including mixed languages. Existing document text stays out of history." },
            { "清空所有历史", "Clear all history" },
            { "保存设置", "Save settings" },
            { "已复制", "Copied" },
            { "界面语言", "Language" },
            { "操作未完成，请重试。历史仍保留在本机。", "Please try again. Your history is still stored locally." },
            { "拾字未能启动：", "TypeTrail could not start: " },
            { "到输入框打字后，这里会显示记录状态", "Start typing in an app to see its recording status" },
            { "已备份并移出 ", "Backed up and archived " },
            { " 条旧版快照；现在只记录新增输入", " old snapshots. Only new input is recorded now." },
            { "旧历史备份未完成，已暂停记录；原文件保持不变", "Recording paused: history backup did not finish. Original files are intact." },
            { "打开输入历史", "Open input history" },
            { "暂停记录", "Pause recording" },
            { "恢复记录", "Resume recording" },
            { "退出拾字", "Quit TypeTrail" },
            { "快捷键被其他程序占用，请设置一个新组合。", "This shortcut is in use. Please choose another." },
            { "输入活动检测未能启动，请退出后重新打开拾字。", "Input detection could not start. Quit and reopen TypeTrail." },
            { "已选中 ", "Selected: " },
            { " 个字符", " characters" },
            { "请按组合键…", "Press your shortcut…" },
            { " · 已记录上屏文字", " · Committed text recorded" },
            { "当前输入框响应较慢，已自动恢复采集服务", "This app was slow to respond. Input capture has restarted." },
            { "采集服务未能启动，重新打开拾字可重试", "Input capture could not start. Reopen TypeTrail to retry." },
            { "QQ · 已记录输入法上屏文字", "QQ · Committed input recorded" },
            { " · 已跳过敏感或只读输入区", " · Sensitive or read-only field skipped" },
            { " · 正在监听上屏文字", " · Listening for committed text" },
            { " · 输入框可记录", " · Recording available" },
            { " · 等待输入", " · Waiting for input" },
            { " · 无法确定输入边界，本次未记录", " · Input boundary unclear; this change was skipped" },
            { " · 正在组词，确认后记录", " · Composing; recording after confirmation" },
            { " · 已跳过密码或敏感输入框", " · Password or sensitive field skipped" },
            { " · 当前文档为只读", " · This document is read-only" },
            { " · 已在排除列表中", " · App excluded" },
            { " · 当前焦点不在可读取的输入区", " · Focus is outside a readable input field" },
            { " · 此编辑区未提供可读取的文本，当前未记录", " · This editor does not expose readable text" },
            { " · 文档接口暂不可用，当前未记录", " · Document interface unavailable" },
            { " · 暂时无法读取输入框", " · Input field temporarily unavailable" },
            { "等待输入", "Waiting for input" },
            { "    随时呼出", "    Open anywhere" },
            { "没有找到这段文字", "No matching words" },
            { "换个关键词，或清空搜索查看全部记录。", "Try another word, or clear the search to see everything." },
            { "当前内容", "Current version" },
            { "已清空", "Cleared" },
            { "当前内容 · 已清空", "Current version · Cleared" },
            { "文字已全部删除，可在上方查看修改前版本", "All text was deleted. Choose an earlier version above to recover it." },
            { "修改前 · ", "Earlier version · " },
            { "MM月dd日  HH:mm:ss", "MMM d, HH:mm:ss" },
            { " 字符", " characters" },
            { "   ·   修改前", "   ·   Earlier version" },
            { "   ·   部分内容", "   ·   Partial text" },
            { "  ·  修改前", "  ·  Earlier version" },
            { "记录已暂停 · 历史仍可查看", "Recording paused · Your history is still available" },
            { "已复制选中的文字", "Selection copied" },
            { "已复制整段文字 · Esc 返回原应用", "Copied · Esc returns to your app" },
            { "剪贴板正忙，请再点一次复制。", "Clipboard is busy. Please try copying again." },
            { "请搭配 Ctrl、Alt 或 Win，避免影响正常打字。", "Include Ctrl, Alt or Win so typing keeps working normally." },
            { "保存时会检查快捷键是否被占用。", "We will check shortcut availability when you save." },
            { "已恢复删除的记录", "Deleted entry restored" },
            { "已删除 · Ctrl + Z 可撤销", "Deleted · Ctrl + Z to undo" },
            { "旧历史尚未完成备份，请检查数据目录后重新打开拾字", "History backup is incomplete. Check the data folder and reopen TypeTrail." },
            { "记录不完整", "Partial recording" },
            { "监听受限", "Recording limited" },
            { "输入监听已恢复，等待输入", "Input monitoring restored; waiting for input" },
            { "当前未记录", "Not recording" },
            { "已跳过", "Skipped" },
            { "等待选词", "Composing" },
            { "正在记录", "Recording" },
            { "Ⅱ  已暂停", "Ⅱ  Paused" },
            { "请先完成快捷键设置，或按 Esc 取消。", "Finish choosing the shortcut, or press Esc to cancel." },
            { "这个快捷键已被占用，请换一个。原快捷键仍保留。", "This shortcut is in use. Your previous shortcut is still active." },
            { "部分设置写入失败，请检查目录权限后重试。", "Some settings could not be saved. Check folder permissions and try again." },
            { "设置已保存", "Settings saved" },
            { "再点一次确认清空", "Click again to confirm" },
            { "内存已清空，但磁盘文件删除失败。", "Memory cleared, but the history file could not be removed." },
            { "历史已清空。", "History cleared." },
            { "拾字 · 本地输入历史", "TypeTrail · Local input history" },
            { "设置暂时无法保存，请检查数据目录。", "Settings could not be saved. Please check the data folder." },
            { "旧历史无法读取，已保留加密文件副本。", "History could not be read. An encrypted backup was preserved." },
            { "旧历史无法读取，请检查数据目录的访问权限。", "History could not be read. Check data folder permissions." },
            { "历史暂时无法写入磁盘，本次记录仍保留在内存。", "History could not be saved to disk. This session remains in memory." },
            { "旧历史备份失败，已跳过碎片整理。", "History backup failed. Cleanup was skipped." },
            { "备份已保存，但旧历史未能移出，已暂停记录。", "Backup saved, but old history could not be archived. Recording paused." },
            { "微信 / QQ 输入监听未启动（", "WeChat / QQ input capture could not start (" },
            { "）", ")" },
            { "输入过快，部分字符未能保存", "Some characters were lost during rapid input" },
            { "输入边界暂未确认，部分字符未能保存", "Some input could not be saved because its field could not be verified" },
            { "输入窗口正在变化，正在重新检查", "Input windows are changing; checking again" },
            { "32位输入同步通道暂不可用", "The 32-bit input synchronization channel is temporarily unavailable" },
        };
        public static string T(string text) { string result; return English && Strings.TryGetValue(text, out result) ? result : text; }
        public static string Display(string source) { return English ? source == "微信" ? "WeChat" : source == "记事本" ? "Notepad" : source == "WPS 文字" ? "WPS Writer" : source == "拾字测试输入框" ? "TypeTrail test field" : source : source; }
        public static string Rewrite(string text) {
            if (text == null) return null;
            foreach (var pair in Strings.OrderByDescending(p => English ? p.Key.Length : p.Value.Length))
                text = text.Replace(English ? pair.Key : pair.Value, English ? pair.Value : pair.Key);
            return text;
        }
        public static void Apply(DependencyObject root) {
            var framework = root as FrameworkElement;
            if (framework != null && framework.ToolTip is string) framework.ToolTip = Rewrite((string)framework.ToolTip);
            // Never translate user history, input fields, app names or search text.
            if (root is ListBox || root is TextBox) return;
            var text = root as TextBlock; if (text != null) text.Text = Rewrite(text.Text);
            var content = root as ContentControl;
            if (content != null && content.Content is string && !(root is ComboBoxItem && ((ComboBoxItem)root).Tag is string && new[]{"auto","zh","en"}.Contains((string)((ComboBoxItem)root).Tag))) content.Content = Rewrite((string)content.Content);
            foreach (object child in LogicalTreeHelper.GetChildren(root)) { var dependency = child as DependencyObject; if (dependency != null) Apply(dependency); }
            var window = root as Window; if (window != null) window.Title = T("拾字 · 输入历史");
        }
    }
}
