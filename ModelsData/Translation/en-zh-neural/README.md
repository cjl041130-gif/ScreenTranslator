# English → 中文神经翻译语言包

本目录包含 `Helsinki-NLP/opus-mt-en-zh` 的 ONNX 量化版本，用于 ScreenTranslator 的本地整句英译中。

- 原始模型：https://huggingface.co/Helsinki-NLP/opus-mt-en-zh
- ONNX 转换：https://huggingface.co/Xenova/opus-mt-en-zh
- 模型架构：MarianMT / OPUS-MT
- 运行时：Microsoft ONNX Runtime CPU
- 许可证：Apache-2.0
- 网络要求：应用运行时不发起网络请求

`manifest.json` 记录了随程序分发的模型、词表和分词器文件的 SHA256。初始化时会逐项校验，缺失或损坏时拒绝启动翻译引擎。
