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

## 电子信息专业词库（`dicts\eedict.json`）

- 术语与中文释义参考：安徽大学电子信息工程学院《专业英语-词汇复习-2026》（<https://github.com/caomax361-ops/professional-english-vocab-2026>，该仓库未声明许可证，仅作术语对照数据收录并在此注明来源）
- 术语与中文详解：ADI（Analog Devices）官方术语表《Glossary of EE Terms》（<https://www.analog.com/cn/design-center/glossary.html>，内容版权归 Analog Devices, Inc. 所有，仅作个人学习用途；本项目收录其社区抓取整理版本 <https://github.com/WangShuXian6/blog/issues/164>）
- 专业词汇中英对照：Phillweston/EE_Vocabulary《电子类专业英语词汇》（<https://github.com/Phillweston/EE_Vocabulary>，该仓库未声明许可证，仅作词汇对照数据收录并在此注明来源）
- 补充词条与硬件/嵌入式概念词条：由本项目整理及撰写解释
- 英文释义兜底：取自 ECDICT（MIT，<https://github.com/skywind3000/ECDICT>）
- 百科链接：仅指向百度百科公开条目链接，不内嵌其内容

两份词库文件通过本仓库的 release `dicts-v1` 分发；若需再分发请一并保留本声明。
