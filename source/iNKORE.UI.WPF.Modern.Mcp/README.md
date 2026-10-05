# iNKORE.UI.WPF.Modern.Mcp

A local [Model Context Protocol](https://modelcontextprotocol.io) server that gives AI assistants accurate, version-matched knowledge of [iNKORE.UI.WPF.Modern](https://github.com/iNKORE-NET/UI.WPF.Modern): its controls, theme resources, documentation and Gallery samples.

The tool's version always matches the library version it was built from. Install the version that matches the `iNKORE.UI.WPF.Modern` package your project references.

```bash
dotnet tool install -g iNKORE.UI.WPF.Modern.Mcp --version <library version>
```

Then register the `inkore-wpf-mcp` command as a stdio MCP server in your client.
