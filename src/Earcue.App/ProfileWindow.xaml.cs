using System.Windows;
using System.Windows.Controls;
using Earcue.App.Services;
using Earcue.Core.Configuration;

namespace Earcue.App;

public partial class ProfileWindow : Window
{
    private readonly ProfileOptions _draft;
    private readonly string _userRoleDescription;
    private bool _initializing = true;

    public ProfileWindow(ProfileOptions profile, string userRoleDescription)
    {
        InitializeComponent();

        _draft = Clone(profile);
        _userRoleDescription = userRoleDescription ?? string.Empty;

        CmbRole.ItemsSource = InterviewRolePresets.All;
        CmbRole.SelectedItem = InterviewRolePresets.Find(_draft.Role);
        TxtUserRole.Text = _userRoleDescription;
        TxtResume.Text = _draft.Resume;

        _initializing = false;
        UpdateRoleUi();
        UpdateResumeCount();
        ClampToWorkArea();
    }

    public ProfileOptions ProfileResult => _draft;

    public string UserRoleDescription => TxtUserRole.Text.Trim();

    private void ClampToWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        var maxWidth = Math.Max(640, workArea.Width - 32);
        var maxHeight = Math.Max(480, workArea.Height - 32);
        MinWidth = Math.Min(MinWidth, maxWidth);
        MinHeight = Math.Min(MinHeight, maxHeight);
        MaxWidth = maxWidth;
        MaxHeight = maxHeight;
        if (Width > MaxWidth)
        {
            Width = MaxWidth;
        }

        if (Height > MaxHeight)
        {
            Height = MaxHeight;
        }
    }

    private void CmbRole_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing)
        {
            UpdateRoleUi();
        }
    }

    private void UpdateRoleUi()
    {
        var preset = CmbRole.SelectedItem as InterviewRolePreset
                     ?? InterviewRolePresets.Find(_draft.Role);

        TxtRoleHint.Text = preset.Description;
        TxtResumeLabel.Text = string.Equals(preset.Id, InterviewRolePresets.Candidate, StringComparison.OrdinalIgnoreCase)
            ? "个人简历（用于回答时引用真实经历）"
            : "岗位要求 / 候选人背景（可选，用于生成追问）";
    }

    private void TxtUserRole_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 只用于保存，无需实时联动
    }

    private void TxtResume_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initializing)
        {
            UpdateResumeCount();
        }
    }

    private void UpdateResumeCount()
    {
        var length = TxtResume.Text?.Length ?? 0;
        TxtResumeCount.Text = length == 0 ? "未填写" : $"{length} 字";
    }

    private void BtnImportResume_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择简历文件",
            Filter = "简历文件|*.txt;*.md;*.markdown;*.docx|文本文件|*.txt;*.md;*.markdown|Word 文档|*.docx|所有文件|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var text = ResumeImporter.Load(dialog.FileName);
            if (string.IsNullOrWhiteSpace(text))
            {
                MessageBox.Show(this, "文件里没有可读取的文字。", "导入简历",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            TxtResume.Text = text;
            TxtResume.CaretIndex = 0;
            TxtResume.ScrollToHome();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "导入简历失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnClearResume_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtResume.Text))
        {
            return;
        }

        var choice = MessageBox.Show(this, "清空当前简历内容？", "确认",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Yes)
        {
            TxtResume.Clear();
            TxtResume.Focus();
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var role = CmbRole.SelectedItem as InterviewRolePreset
                   ?? InterviewRolePresets.Find(_draft.Role);

        _draft.Role = role.Id;
        _draft.Resume = TxtResume.Text.Trim();
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private static ProfileOptions Clone(ProfileOptions source) => new()
    {
        Role = source.Role,
        Resume = source.Resume,
        MaxResumeChars = source.MaxResumeChars
    };
}
