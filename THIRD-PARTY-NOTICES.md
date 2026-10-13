# Third-Party Notices

本仓库源代码不含随附的第三方代码；离线词库数据在**构建时**由下列开源项目合并生成（仓库本身不分发词库产物）：

## qwerty-learner

- 来源：<https://github.com/Kaiyiwing/qwerty-learner>
- 许可：GPL-3.0
- 用途：英语词库（`src/resources` 下的词典索引）

## ECDICT

- 来源：<https://github.com/skywind3000/ECDICT>
- 许可：MIT
- 用途：英汉词典数据（音标、释义、词频）

## curated.tsv

- 来源：本仓库 `tools/Qwaacty.LexiconBuilder/curated.tsv`
- 内容：少量人工校对的高频词汇补充

由上述数据合并生成的 `dicts\lexicon.ttlx` 属于衍生数据；对外分发该产物时请同时遵守 qwerty-learner（GPL-3.0）与 ECDICT（MIT）的许可要求。
