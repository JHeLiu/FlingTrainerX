using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using FlingTrainerX.Services;

namespace FlingTrainerX.Models;

public enum DownloadState
{
    Queued,
    Running,
    Completed,
    Failed,
    Canceled
}

/// <summary>下载队列里的一条任务：一个官网文件对应一条，进度/速度/状态都在这里。</summary>
public sealed class DownloadTask : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public DownloadTask(TrainerItem item, TrainerDownload file)
    {
        TrainerId = item.Id;
        GameTitle = item.DisplayTitle;
        ArticleUrl = item.Url;
        FileName = string.IsNullOrWhiteSpace(file.File) ? "trainer" : file.File;
        Url = file.Url;
        Ext = string.IsNullOrWhiteSpace(file.Ext) ? "zip" : file.Ext;
        SiteDate = file.Date;
        SizeHint = file.Size;
        ExpectedBytes = ParseSize(file.Size);
    }

    public int TrainerId { get; }
    public string GameTitle { get; }
    public string ArticleUrl { get; }
    public string FileName { get; }
    public string Url { get; }
    public string Ext { get; }
    public string SiteDate { get; }
    public string SizeHint { get; }

    /// <summary>官网标注的大小，服务器不给 Content-Length 时用它当进度条分母（约等于）。</summary>
    public long ExpectedBytes { get; }

    private DownloadState _state = DownloadState.Queued;
    private long _received;
    private long _total;
    private double _speed;
    private string _savePath = "";
    private string _error = "";

    public DownloadState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            Notify(nameof(State));
            Notify(nameof(StateText));
            Notify(nameof(IsActive));
            Notify(nameof(IsFinished));
            Notify(nameof(IsCompleted));
            Notify(nameof(CanCancel));
            Notify(nameof(CanRetry));
            Notify(nameof(Progress));
            Notify(nameof(ProgressText));
            Notify(nameof(Indeterminate));
            Notify(nameof(DetailText));
            Notify(nameof(RemainingText));
        }
    }

    /// <summary>已下载字节数。</summary>
    public long Received
    {
        get => _received;
        set
        {
            if (_received == value) return;
            _received = value;
            Notify(nameof(Received));
            Notify(nameof(Progress));
            Notify(nameof(ProgressText));
            Notify(nameof(DetailText));
            Notify(nameof(RemainingText));
        }
    }

    /// <summary>总字节数，0 表示服务器没给（chunked）。</summary>
    public long Total
    {
        get => _total;
        set
        {
            if (_total == value) return;
            _total = value;
            Notify(nameof(Total));
            Notify(nameof(Progress));
            Notify(nameof(ProgressText));
            Notify(nameof(Indeterminate));
        }
    }

    public double Speed
    {
        get => _speed;
        set
        {
            if (Math.Abs(_speed - value) < 1) return;
            _speed = value;
            Notify(nameof(Speed));
            Notify(nameof(SpeedText));
            Notify(nameof(DetailText));
            Notify(nameof(RemainingText));
        }
    }

    public string SavePath
    {
        get => _savePath;
        set
        {
            if (_savePath == value) return;
            _savePath = value;
            Notify(nameof(SavePath));
        }
    }

    public string Error
    {
        get => _error;
        set
        {
            if (_error == value) return;
            _error = value;
            Notify(nameof(Error));
            Notify(nameof(StateText));
            Notify(nameof(DetailText));
        }
    }

    // ---------- 展示用 ----------

    /// <summary>进度分母：只认服务器给的真实长度。官网是 PHP 流式转发，基本都拿不到总大小。</summary>
    private long Denominator => Total > 0 ? Total : ExpectedBytes;

    /// <summary>拿不到总大小时不装进度，走滚动条（否则会一直超过 100%）。</summary>
    public bool Indeterminate => State == DownloadState.Running && Total <= 0;

    public double Progress
    {
        get
        {
            if (State == DownloadState.Completed) return 1;
            if (Total <= 0) return 0;
            return Math.Min(0.999, _received / (double)Total);
        }
    }

    public string ProgressText => State switch
    {
        DownloadState.Completed => "100%",
        DownloadState.Running => Total > 0 ? $"{Progress * 100:0}%" : "",
        DownloadState.Failed => "失败",
        DownloadState.Canceled => "已取消",
        _ => "等待中"
    };

    public string StateText => State switch
    {
        DownloadState.Queued => "排队中",
        DownloadState.Running => "下载中",
        DownloadState.Completed => "已完成",
        DownloadState.Failed => "下载失败",
        _ => "已取消"
    };

    public string SpeedText => State == DownloadState.Running && _speed > 0
        ? $"{Human((long)_speed)}/s"
        : "";

    /// <summary>进度条下面那行小字：已下载 / 总量 · 速度 · 剩余。</summary>
    public string DetailText
    {
        get
        {
            switch (State)
            {
                case DownloadState.Failed:
                    return Error.Length > 0 ? Error : "下载中断，可重试";
                case DownloadState.Completed:
                    return $"{Human(_received)} · 已保存到 {System.IO.Path.GetFileName(SavePath)}";
                case DownloadState.Canceled:
                    return "已取消，可重新下载";
                case DownloadState.Queued:
                    return "排队中，等待空闲下载位";
            }

            var size = Total > 0 ? $"{Human(_received)} / {Human(Total)}"
                : (_received == 0 ? "正在连接服务器…" : $"已下载 {Human(_received)}");

            var parts = new List<string> { size };
            if (_speed > 0) parts.Add(SpeedText);
            if (RemainingText.Length > 0) parts.Add(RemainingText);
            return string.Join(" · ", parts);
        }
    }

    public string RemainingText
    {
        get
        {
            var total = Denominator;
            if (State != DownloadState.Running || _speed < 1024 || total <= 0) return "";

            var left = (total - _received) / _speed;
            return left switch
            {
                < 1 => "即将完成",
                < 60 => $"剩余 {left:0} 秒",
                _ => $"剩余 {left / 60:0.0} 分钟"
            };
        }
    }

    public bool IsActive => State is DownloadState.Queued or DownloadState.Running;
    public bool IsFinished => State is DownloadState.Completed or DownloadState.Failed or DownloadState.Canceled;
    public bool IsCompleted => State == DownloadState.Completed;
    public bool CanCancel => State is DownloadState.Queued or DownloadState.Running;
    public bool CanRetry => State is DownloadState.Failed or DownloadState.Canceled;

    public string ExtText => Ext.ToUpperInvariant();

    public string Key => $"{TrainerId}|{Url}";

    internal void Notify(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>把 "950 KB" / "1.2 MB" 这种官网文案换算成字节，用于没有 Content-Length 时的进度估算。</summary>
    private static long ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        var number = new System.Text.StringBuilder();
        var unit = new System.Text.StringBuilder();

        foreach (var c in text.Trim())
        {
            if (char.IsDigit(c) || c == '.' || c == ',') number.Append(c == ',' ? '.' : c);
            else if (char.IsLetter(c)) unit.Append(c);
        }

        if (!double.TryParse(number.ToString(), out var value) || value <= 0) return 0;

        var scale = unit.ToString().ToUpperInvariant() switch
        {
            "B" => 1d,
            "KB" => 1024d,
            "MB" => 1024d * 1024,
            "GB" => 1024d * 1024 * 1024,
            _ => 0d
        };

        return (long)(value * scale);
    }

    /// <summary>字节数的可读写法。</summary>
    public static string Human(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024d / 1024:0.0} MB";
        return $"{bytes / 1024d / 1024 / 1024:0.00} GB";
    }
}
