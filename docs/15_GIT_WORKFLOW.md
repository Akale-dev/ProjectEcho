# 15｜Git 使用与敏感文件

已有Git就复用；没有时可用经核验的Portable Git，不修改全局PATH。开发不以GitHub远程仓库为前提。

## 初始化（只在本项目中）
```powershell
git status
git init
git status --short
```
已有仓库不要重新组织历史；不改全局姓名/邮箱。Git缺identity时说明并让用户决定项目级identity，不使用虚构身份。

## 每阶段本地检查点
先编译/测试→`git diff`检查→确认没有会议资料/浏览器profile/密钥→按实际改动选择文件：
```powershell
git add AGENTS.md README.md STATUS.md docs prompts schemas examples scripts reference tests REQUIREMENTS.csv .gitignore .gitattributes
# M0之后再加入实际src与解决方案；这里的列表不是自动上传授权。
git diff --cached --stat
git diff --cached --check
git commit -m "docs: add ProjectEcho development handoff"
```
新增源代码按实际路径加入。不要盲目`git add -f`录音/模型。不运行强制push、reset --hard或git clean -fdx。

## 远程
本包不创建仓库、不设置remote、不push。后续用户明确授权上传代码时，先检查已跟踪/待提交内容、remote和分支；再按实际仓库操作。私有仓库也不能放入未经授权会议音频。

## .gitignore 只是防误入
它不能保护已经提交的内容。发现秘密/会议资料已被跟踪应停下并报告，不能仅加ignore声称已经删除历史。默认会议数据根在仓库外；BrowserProfiles、.local、模型和二进制都忽略。
