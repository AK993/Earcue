# ADR-010: 面试角色与简历上下文

## 背景

会议助手切到面试场景后，通用 Prompt 有两个明显问题：

1. **不知道“我”是谁**：回答面试官提问时，模型只能给通用套话，无法引用我的真实项目、年限、技术栈。
2. **角色可能相反**：我用它准备面试（被面试者）和用它当面试官（面试者）时，需要的输出完全不同——前者要“怎么回答”，后者要“下一步问什么”。

同时，简历通常是 Word/PDF 文件，让用户每次开会前重新粘贴不现实。

## 决策

### 1. 新增“面试资料”独立窗口

主界面右上角新增 **👤 面试资料**，打开 [ProfileWindow](../../src/Earcue.App/ProfileWindow.xaml)：

- **角色**：被面试者（候选人）/ 面试者（面试官）；
- **岗位 / 身份补充**（可选，复用 `llm.userRoleDescription`）；
- **个人简历 / 背景资料**：可直接粘贴，或导入 `.txt` / `.md` / `.docx`；
- 简历字数实时显示；PDF 提示用户复制文本后粘贴。

### 2. 配置保存到本机

```json
"profile": {
  "role": "Candidate",
  "resume": "……",
  "maxResumeChars": 2500
}
```

- 角色默认 `Candidate`（被面试者/候选人），兼容用户只关心“帮我回答”的场景；
- 简历只保存在本机 `config.json`，不会单独上传；
- 发送给模型时按 `maxResumeChars` 截断（默认 2500 字），避免上下文和费用失控；
- 简历是个人敏感资料，目前是本地明文（与 API Key 的 DPAPI 加密不同）；后续如有需要可改为加密存储。

### 3. Prompt 按角色注入

`Prompt` 新增 `Role`、`ResumeText`、`MaxResumeChars`：

- **候选人**：系统提示词明确“我是被面试者”，把简历放在 `【我的简历】`，要求“优先结合真实经历，简历没有的不要编造”，用第一人称、口语、长度按内容需要给出回答。
- **面试官**：系统提示词明确“我是面试官”，把简历字段作为 `【岗位要求 / 候选人背景】`，要求“根据候选人刚说的内容给出下一步追问或简短评估，不要替候选人编造经历”。
- 自动应答模式同样按角色分支：候选人回答，面试官追问；都保留“不是提问也可能是在点名让我发言/自我介绍”的处理。

### 4. 简历导入

`ResumeImporter` 支持：

- `.txt` / `.md`：直接读取；
- `.docx`：用 `ZipArchive` 读取 `word/document.xml`，按段落提取文字（不引入额外 Office 依赖）；
- 单文件上限 10 MB；
- PDF 不支持自动解析，提示复制文本粘贴。

## 备选方案

- **把简历写进系统 Prompt 常量**：无法因人而异；否决。
- **每次面试前手动粘贴**：麻烦且容易漏；改为持久化 + 文件导入。
- **引入 PDF/Word 解析库**：PDF 文本抽取复杂、依赖重；Word 先用无依赖的 docx 解析，PDF 引导粘贴。
- **简历加密存储**：更安全，但简历本身不是密钥，当前先保证可用；后续可像 API Key 一样 DPAPI 加密。

## 后果

- **Token 成本增加**：每次请求会带上最多 2500 字简历；答案长度按内容需要，不再压到 40 字。
- **回答风格随角色变化**：切到面试官模式后，输出是追问/评估，不再替候选人回答。
- **简历截断**：超过 2500 字的部分不会发送；用户需要把最关键的经历放在前面。
- **本地明文**：简历在 `config.json` 中明文保存；分享配置/诊断包时需注意脱敏。
- **PDF 不支持**：用户需复制粘贴；后续可接 PDF 文本抽取。

## 涉及代码

- `src/Earcue.Core/Models/InterviewRole.cs`：角色枚举。
- `src/Earcue.Core/Configuration/InterviewRolePresets.cs`：角色预设与映射。
- `src/Earcue.Core/Configuration/EarcueConfig.cs`：`ProfileOptions`。
- `src/Earcue.Core/Models/Prompt.cs`：角色/简历注入与角色化 Prompt。
- `src/Earcue.App/ProfileWindow.xaml{,.cs}`：面试资料设置窗口。
- `src/Earcue.App/Services/ResumeImporter.cs`：txt/md/docx 导入。
- `src/Earcue.App/MainWindow.xaml{,.cs}`：👤 面试资料入口、Prompt 装配。
