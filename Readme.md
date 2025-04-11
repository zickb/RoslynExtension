# RosylnExtension

This extension enables symbol store and source link capabilities ("go to original source") for the standalone c# lsp server.
Because the extension implements an internal interface of roslyn, there could be breaks in the future.
For development, it is recommended to build the code first so that all "internal" dependencies can be successfully resolved by roslyn.

The extension could be tested in vscode:

1. install the c# extension
2. disable (if installed) the devkit extension
3. add the following setting ```"dotnet.server.extensionPaths": [
        <path-to-extension>"
    ]```
