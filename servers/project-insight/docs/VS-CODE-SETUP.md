# VS Code Setup Guide - Project Insight MCP Server

## 🚀 **Quick Start**

Your Project Insight MCP server is now fully configured for VS Code development!

### **1. Open in VS Code**
```bash
# Option A: Open workspace file
code ProjectInsightMCP.code-workspace

# Option B: Open folder
code .
```

### **2. Install Recommended Extensions**
VS Code will prompt you to install recommended extensions, or install manually:
- **C# Dev Kit** - Essential for .NET development
- **Azure Functions** - For Azure Functions development and debugging
- **REST Client** - For testing HTTP endpoints directly in VS Code

## 🛠️ **Available VS Code Tasks**

Press `Cmd+Shift+P` (macOS) or `Ctrl+Shift+P` (Windows/Linux) and type "Tasks: Run Task":

### **Build & Development**
- **`build`** - Build the project
- **`clean`** - Clean build artifacts  
- **`restore`** - Restore NuGet packages
- **`func: start`** - Start Azure Functions runtime (with auto-build)
- **`func: stop`** - Stop running Azure Functions

### **Testing**
- **`test: health`** - Quick health check
- **`test: mcp tools`** - Test MCP tools list
- **`test: search projects`** - Test project search
- **`test: full suite`** - Run complete test suite

### **Deployment**
- **`deploy to azure`** - Deploy to Azure Functions

## 🐛 **Debugging**

### **Launch Configurations Available:**

1. **`Launch Azure Functions`** - Start with debugger attached
2. **`Attach to Azure Functions`** - Attach to running instance
3. **`Debug .NET Core`** - Direct .NET debugging

### **How to Debug:**
1. Set breakpoints in your code
2. Press `F5` or go to Run & Debug panel
3. Select "Launch Azure Functions"
4. The debugger will start and hit your breakpoints

## 🧪 **Testing from VS Code**

### **Method 1: REST Client (Recommended)**
1. Open `test-requests.http`
2. Click "Send Request" above any HTTP request
3. View responses inline

### **Method 2: Tasks**
1. `Cmd+Shift+P` → "Tasks: Run Task"
2. Select any test task
3. View output in terminal

### **Method 3: Integrated Terminal**
```bash
# Start the function
func start

# In another terminal
./quick-test-mcp.sh
```

## 📁 **VS Code Configuration Files**

| File | Purpose |
|------|---------|
| `.vscode/tasks.json` | Build, test, and deployment tasks |
| `.vscode/launch.json` | Debug configurations |
| `.vscode/settings.json` | Project-specific settings |
| `.vscode/extensions.json` | Recommended extensions |
| `test-requests.http` | HTTP test requests |
| `ProjectInsightMCP.code-workspace` | Workspace definition |

## ⚡ **Quick Development Workflow**

### **Daily Development:**
1. **Open VS Code**: `code ProjectInsightMCP.code-workspace`
2. **Start Function**: `Cmd+Shift+P` → "Tasks: Run Task" → "func: start"
3. **Test Changes**: Open `test-requests.http` and click "Send Request"
4. **Debug Issues**: Set breakpoints and press `F5`

### **Testing Workflow:**
1. **Health Check**: Run task "test: health"
2. **MCP Validation**: Run task "test: mcp tools"  
3. **Full Test**: Run task "test: full suite"
4. **Interactive Testing**: Use `test-requests.http`

## 🔧 **Keyboard Shortcuts**

| Action | Shortcut | Task |
|--------|----------|------|
| Build | `Cmd+Shift+B` | Build project |
| Debug | `F5` | Start debugging |
| Command Palette | `Cmd+Shift+P` | Access tasks/commands |
| Terminal | `Cmd+`` | Open integrated terminal |
| Problems | `Cmd+Shift+M` | View build errors |

## 📊 **Monitoring & Logs**

### **View Function Logs:**
1. Start function with task "func: start"
2. Watch output in Terminal panel
3. View detailed logs in integrated terminal

### **Health Monitoring:**
- **Quick Check**: Run "test: health" task
- **Detailed Check**: Open `test-requests.http` and use health endpoints
- **Real-time**: Keep terminal open while running functions

## 🚀 **Deployment from VS Code**

### **Azure Deployment:**
1. **Configure**: Edit `deploy-params.json` if needed
2. **Deploy**: Run task "deploy to azure"
3. **Monitor**: Watch deployment progress in terminal
4. **Test**: Use `test-deployment.sh` script

### **Quick Deploy:**
```bash
# From VS Code terminal
./deploy.sh
```

## 🔍 **Troubleshooting**

### **Common Issues:**

**Function won't start:**
- Run "clean" task then "build" task
- Check `.NET 8.0` is installed
- Verify Azure Functions Core Tools v4

**Debugging not working:**
- Ensure C# Dev Kit extension is installed
- Check launch configuration in `.vscode/launch.json`
- Restart VS Code

**Tests failing:**
- Verify function is running with "func: start" task
- Check API credentials in `local.settings.json`
- Run "test: health" to diagnose

**Build errors:**
- Run "restore" task to update packages
- Check Problems panel (`Cmd+Shift+M`)
- Verify .NET SDK version

## 📋 **VS Code Features Enabled**

✅ **IntelliSense** - Full C# code completion  
✅ **Debugging** - Breakpoints and step-through debugging  
✅ **Task Runner** - Build, test, and deploy tasks  
✅ **HTTP Testing** - REST Client integration  
✅ **Azure Integration** - Azure Functions extension  
✅ **Git Integration** - Source control panel  
✅ **Terminal** - Integrated terminal for commands  
✅ **Error Highlighting** - Real-time error detection  

## 🎯 **Pro Tips**

1. **Multi-Terminal**: Use split terminal for function + testing
2. **Watch Mode**: Keep "func: start" running while developing
3. **REST Client**: Use `test-requests.http` for quick API testing
4. **Debugging**: Set breakpoints in MCP handlers for debugging
5. **Task Shortcuts**: Create custom keybindings for frequent tasks

## ✅ **Ready to Code!**

Your VS Code environment is fully configured for Project Insight MCP server development. You can now:

- 🔧 **Build and debug** with full IDE support
- 🧪 **Test HTTP endpoints** directly in editor
- 🚀 **Deploy to Azure** with one command
- 📊 **Monitor health** and performance
- 🔍 **Debug issues** with breakpoints

**Happy coding!** 🎉

---

*Start by opening the workspace file and running the "func: start" task to get going!*