# RosylnExtension

For the extension to work, the Roslyn source code must be adapted in 1 places:
1. the visibility of the `ISourceLinkService` interface and the methods defined in the inteface must be changed from `internal` to `public`

The extension could be tested in vscode:
1. install the c# extension
2. disable (if installed) the devkit extension
3. add the following setting `"dotnet.server.path": "<path to language server dll with the modifications>"`
