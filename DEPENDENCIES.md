# ビルド

Windows x64、.NET SDK 10.0.302、Dalamud API 15の参照DLLを用意してください。
プロジェクトルートで次のコマンドを実行します。

~~~powershell
dotnet build src/SpendMeMaybe.csproj -c Release -p:Distribution=true '-p:DalamudLibPath=<Dalamud SDKのフォルダー>'
~~~

MinasisUIはvendorの固定DLLを使います。採用版とSHA256は同じフォルダーのADOPTION.jsonに記載しています。SDK DLLはDalamudが提供します。
