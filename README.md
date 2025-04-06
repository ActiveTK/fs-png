# 🧊 fs-png - PNG Based Secret File System ✨

## 📝 概要

📦 **PNGファイルをそのまま「仮想ディスク」としてマウントできるステガノグラフィアプリ**です。

Windows環境で、PNG画像の中にファイルやディレクトリを隠して保存したり、持ち運ぶことができます。

## 🎁 主な特徴

- 🖼️ **PNGファイルにファイルシステムを格納**
  - 独自チャンク `fsMT`, `fsDF` を使ってディレクトリ構造とファイルを保存

- 🔒 **仮想メモリファイルシステム**
  - Dokanを用いて仮想ドライブとしてマウント可能（例: `P:\`）

- 🚀 **メモリの帯域を生かした爆速ストレージ**
  - 小～中サイズのファイルはメモリ上に展開されるため、読み書きが圧倒的に高速

- 💾 **マウント中に自動保存＆復元**
  - 変更は一定時間で自動書き込みされ、再マウント時に自動復元！

- 🖥️ **GUI付きで操作が簡単**
  - PNG選択・サイズ指定・マウントはボタン1つ！

## 🛠️ 使い方

###  1. Dokanyの準備

初めに、[こちら](https://github.com/dokan-dev/dokany/releases/download/v2.2.1.1000/DokanSetup.exe)からDokanyをインストールしてください。

###  2. リポジトリの複製

次に、本リポジトリを複製するか、[こちら](https://github.com/ActiveTK/fs-png/archive/refs/heads/main.zip)からダウンロードして展開してください。

```bash
git clone https://github.com/ActiveTK/fs-png
cd fs-png/bin/
```

### 3. fs-pngの実行

1. `bin/fs-png.exe` を起動
2. PNGファイルを指定
3. 「マウント」ボタンをクリック！
4. 🎈 仮想ドライブ `P:\` が出現！普通のドライブのように使えます！ 🎈

## ⚠️ 注意事項

- 🔥 大量ファイルを扱うとメモリ消費が増加します
- 💾 基本は自動保存ですが、**プロセスが強制終了された場合は保存されないこともあります。**

## 📂 チャンク構造について

| チャンク名 | 内容 |
|------------|------|
| `fsMT`     | ファイルシステムの構造データ (File System Master Table) |
| `fsDF`     | 各ファイルのバイナリデータ本体 (File System Data File) |

## 📦 必要ライブラリ・依存

- 🧩 [.NET Framework v4.7.2（Windows）](https://dotnet.microsoft.com/)
- 📦 [Dokan.NET](https://github.com/dokan-dev/dokan-dotnet)

※ 本アプリは Windows 専用です

## 📄 ライセンス

このプログラムは The MIT License の下で公開されています。

© 2025 ActiveTK.  
🔗 https://github.com/ActiveTK/gff/blob/master/LICENSE
