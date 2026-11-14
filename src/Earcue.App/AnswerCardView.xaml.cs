using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Earcue.Brain.Understanding;

namespace Earcue.App;

public partial class AnswerCardView : UserControl
{
    public event Action? StopRequested;
    public event Action? RetryRequested;
    public string Question { get; }
    public bool Automatic { get; }
    public string Answer => TxtAnswer.Text;
    private bool _hasPreparedQuestion;
    public string? QuestionForRetry => _hasPreparedQuestion || !string.Equals(TxtQuestion.Text, Question, StringComparison.Ordinal)
        ? (string.IsNullOrWhiteSpace(TxtQuestion.Text) ? null : TxtQuestion.Text.Trim()) : null;

    public AnswerCardView(string question, bool automatic)
    {
        InitializeComponent();
        Question = question;
        Automatic = automatic;
        TxtQuestion.Text = question;
        TxtOriginalQuestion.Text = question;
        TxtAnswer.Text = "正在思考…";
    }

    public void PrepareQuestion(bool enabled)
    {
        if (!enabled) return;
        _hasPreparedQuestion = false;
        TxtQuestion.Text = Question;
        TxtQuestionLabel.Text = "正在提取并校正问题…";
        OriginalTextExpander.Visibility = Visibility.Visible;
    }

    public void SetQuestionOverride(string question)
    {
        _hasPreparedQuestion = true;
        TxtQuestion.Text = question;
        TxtQuestionLabel.Text = "本次回答的问题 · 可修改后重答";
        OriginalTextExpander.Visibility = Visibility.Visible;
    }

    public void SetQuestionExtraction(QuestionExtraction? result, bool fallback)
    {
        OriginalTextExpander.Visibility = Visibility.Visible;
        _hasPreparedQuestion = result?.Questions.Count > 0;
        TxtQuestion.Text = _hasPreparedQuestion ? result!.Text : Question;
        TxtQuestionLabel.Text = fallback ? "保留原文 · 本次未完成问题校正"
            : !_hasPreparedQuestion ? "本轮发言 · 未检测到明确问题"
            : result!.Uncertain ? "提取并校正的问题 · 含不确定词，请核对"
            : "提取并校正的问题 · 可修改后重答";
    }

    public void Update(string answer, string status)
    {
        TxtAnswer.Text = answer;
        TxtAnswer.Visibility = string.IsNullOrEmpty(answer) ? Visibility.Collapsed : Visibility.Visible;
        TxtStatus.Text = status;
        BtnCopy.IsEnabled = !string.IsNullOrWhiteSpace(answer);
        BtnCopy.Content = "复制";
    }

    public void SetBusy(bool anyBusy, bool isCurrent)
    {
        BtnRetry.IsEnabled = !anyBusy;
        TxtQuestion.IsReadOnly = anyBusy;
        BtnStop.Visibility = isCurrent ? Visibility.Visible : Visibility.Collapsed;
        BtnStop.IsEnabled = true;
        BtnStop.Content = "停止生成";
    }

    public void SetStopping()
    {
        BtnStop.IsEnabled = false;
        BtnStop.Content = "正在停止…";
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e) => StopRequested?.Invoke();
    private void BtnRetry_Click(object sender, RoutedEventArgs e) => RetryRequested?.Invoke();

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(Answer);
            BtnCopy.Content = "已复制";
        }
        catch (COMException)
        {
            BtnCopy.Content = "请重试复制";
        }
    }
}
