.PHONY: build build-cli build-gui build-sdk build-core build-http build-all \
        release release-cli release-gui install install-cli install-gui uninstall uninstall-cli uninstall-gui \
        clean clean-all test test-core test-http test-all \
        run run-gui dev dev-gui \
        publish-linux publish-windows publish-macos \
        plugin plugin-example plugin-install plugin-clean \
        sdk-pack help

# Directories
BUILD_DIR = src/Modular.Cli/bin/Release/net8.0/linux-x64
GUI_BUILD_DIR = src/Modular.Gui/bin/Release/net8.0
PUBLISH_DIR = publish
INSTALL_DIR = $(HOME)/.local/bin
INSTALL_SHARE_DIR = $(HOME)/.local/share
CONFIG_DIR = $(HOME)/.config/Modular
PLUGIN_DIR = $(CONFIG_DIR)/plugins

# Runtime identifiers
RID_LINUX = linux-x64
RID_WINDOWS = win-x64
RID_MACOS = osx-x64
RID_MACOS_ARM = osx-arm64

# Default target
all: build

##@ Build Targets

build: ## Build all projects in Debug mode
	dotnet build Modular.sln

build-cli: ## Build CLI only
	dotnet build src/Modular.Cli/Modular.Cli.csproj

build-gui: ## Build GUI only
	dotnet build src/Modular.Gui/Modular.Gui.csproj

build-sdk: ## Build SDK only
	dotnet build src/Modular.Sdk/Modular.Sdk.csproj

build-core: ## Build Core library only
	dotnet build src/Modular.Core/Modular.Core.csproj

build-http: ## Build FluentHttp library only
	dotnet build src/Modular.FluentHttp/Modular.FluentHttp.csproj

build-all: ## Build everything including examples
	dotnet build Modular.sln
	dotnet build examples/ExamplePlugin/ExamplePlugin.csproj

##@ Release Targets

release: release-cli ## Build CLI release (alias)

release-cli: ## Build CLI for Linux (self-contained, single file)
	dotnet publish src/Modular.Cli/Modular.Cli.csproj \
		-c Release \
		-r $(RID_LINUX) \
		--self-contained \
		-p:PublishSingleFile=true \
		-p:IncludeNativeLibrariesForSelfExtract=true \
		-o $(PUBLISH_DIR)/cli-linux

release-gui: ## Build GUI for Linux (self-contained)
	dotnet publish src/Modular.Gui/Modular.Gui.csproj \
		-c Release \
		-r $(RID_LINUX) \
		--self-contained \
		-o $(PUBLISH_DIR)/gui-linux

##@ Install Targets

install: install-cli ## Install CLI (alias)

install-cli: release-cli ## Install CLI to ~/.local/bin
	mkdir -p $(INSTALL_DIR)
	cp $(PUBLISH_DIR)/cli-linux/modular $(INSTALL_DIR)/modular
	chmod +x $(INSTALL_DIR)/modular
	@echo "Modular CLI installed to $(INSTALL_DIR)/modular"
	@echo "Make sure $(INSTALL_DIR) is in your PATH"

install-gui: release-gui ## Install GUI to ~/.local/bin and create desktop entry
	mkdir -p $(INSTALL_DIR)
	mkdir -p $(INSTALL_SHARE_DIR)/modular-gui
	cp -r $(PUBLISH_DIR)/gui-linux/* $(INSTALL_SHARE_DIR)/modular-gui/
	ln -sf $(INSTALL_SHARE_DIR)/modular-gui/Modular.Gui $(INSTALL_DIR)/modular-gui
	chmod +x $(INSTALL_DIR)/modular-gui
	@echo "Modular GUI installed to $(INSTALL_DIR)/modular-gui"

install-desktop: ## Create desktop entry for GUI
	mkdir -p $(HOME)/.local/share/applications
	@echo "[Desktop Entry]" > $(HOME)/.local/share/applications/modular.desktop
	@echo "Type=Application" >> $(HOME)/.local/share/applications/modular.desktop
	@echo "Name=Modular Mod Manager" >> $(HOME)/.local/share/applications/modular.desktop
	@echo "Comment=Next-generation mod manager with plugin support" >> $(HOME)/.local/share/applications/modular.desktop
	@echo "Exec=$(INSTALL_DIR)/modular-gui" >> $(HOME)/.local/share/applications/modular.desktop
	@echo "Icon=applications-games" >> $(HOME)/.local/share/applications/modular.desktop
	@echo "Terminal=false" >> $(HOME)/.local/share/applications/modular.desktop
	@echo "Categories=Game;Utility;" >> $(HOME)/.local/share/applications/modular.desktop
	@echo "Keywords=mod;manager;game;" >> $(HOME)/.local/share/applications/modular.desktop
	update-desktop-database $(HOME)/.local/share/applications 2>/dev/null || true
	@echo "Desktop entry created"

##@ Uninstall Targets

uninstall: uninstall-cli ## Uninstall CLI (alias)

uninstall-cli: ## Uninstall CLI from ~/.local/bin
	rm -f $(INSTALL_DIR)/modular
	@echo "Modular CLI uninstalled"

uninstall-gui: ## Uninstall GUI from ~/.local/bin
	rm -f $(INSTALL_DIR)/modular-gui
	rm -rf $(INSTALL_SHARE_DIR)/modular-gui
	rm -f $(HOME)/.local/share/applications/modular.desktop
	update-desktop-database $(HOME)/.local/share/applications 2>/dev/null || true
	@echo "Modular GUI uninstalled"

uninstall-all: uninstall-cli uninstall-gui ## Uninstall everything
	@echo "All Modular components uninstalled"

##@ Run Targets

run: ## Run CLI in development mode
	dotnet run --project src/Modular.Cli/Modular.Cli.csproj

run-gui: ## Run GUI in development mode
	dotnet run --project src/Modular.Gui/Modular.Gui.csproj

dev: run ## Alias for run

dev-gui: run-gui ## Alias for run-gui

##@ Test Targets

test: ## Run all tests
	dotnet test Modular.sln

test-core: ## Run Core library tests only
	dotnet test tests/Modular.Core.Tests/Modular.Core.Tests.csproj

test-http: ## Run FluentHttp tests only
	dotnet test tests/Modular.FluentHttp.Tests/Modular.FluentHttp.Tests.csproj

test-all: test ## Alias for test

##@ Clean Targets

clean: ## Clean build artifacts
	dotnet clean Modular.sln
	rm -rf src/*/bin src/*/obj tests/*/bin tests/*/obj

clean-all: clean ## Clean everything including publish directory
	rm -rf $(PUBLISH_DIR)
	rm -rf examples/*/bin examples/*/obj

##@ Publish Targets (Cross-platform)

publish-linux: ## Publish CLI and GUI for Linux
	mkdir -p $(PUBLISH_DIR)/linux
	dotnet publish src/Modular.Cli/Modular.Cli.csproj -c Release -r $(RID_LINUX) --self-contained -p:PublishSingleFile=true -o $(PUBLISH_DIR)/linux/cli
	dotnet publish src/Modular.Gui/Modular.Gui.csproj -c Release -r $(RID_LINUX) --self-contained -o $(PUBLISH_DIR)/linux/gui
	@echo "Linux builds published to $(PUBLISH_DIR)/linux/"

publish-windows: ## Publish CLI and GUI for Windows
	mkdir -p $(PUBLISH_DIR)/windows
	dotnet publish src/Modular.Cli/Modular.Cli.csproj -c Release -r $(RID_WINDOWS) --self-contained -p:PublishSingleFile=true -o $(PUBLISH_DIR)/windows/cli
	dotnet publish src/Modular.Gui/Modular.Gui.csproj -c Release -r $(RID_WINDOWS) --self-contained -o $(PUBLISH_DIR)/windows/gui
	@echo "Windows builds published to $(PUBLISH_DIR)/windows/"

publish-macos: ## Publish CLI and GUI for macOS (Intel)
	mkdir -p $(PUBLISH_DIR)/macos
	dotnet publish src/Modular.Cli/Modular.Cli.csproj -c Release -r $(RID_MACOS) --self-contained -p:PublishSingleFile=true -o $(PUBLISH_DIR)/macos/cli
	dotnet publish src/Modular.Gui/Modular.Gui.csproj -c Release -r $(RID_MACOS) --self-contained -o $(PUBLISH_DIR)/macos/gui
	@echo "macOS builds published to $(PUBLISH_DIR)/macos/"

publish-macos-arm: ## Publish CLI and GUI for macOS (Apple Silicon)
	mkdir -p $(PUBLISH_DIR)/macos-arm
	dotnet publish src/Modular.Cli/Modular.Cli.csproj -c Release -r $(RID_MACOS_ARM) --self-contained -p:PublishSingleFile=true -o $(PUBLISH_DIR)/macos-arm/cli
	dotnet publish src/Modular.Gui/Modular.Gui.csproj -c Release -r $(RID_MACOS_ARM) --self-contained -o $(PUBLISH_DIR)/macos-arm/gui
	@echo "macOS ARM builds published to $(PUBLISH_DIR)/macos-arm/"

publish-all: publish-linux publish-windows publish-macos publish-macos-arm ## Publish for all platforms
	@echo "All platforms published to $(PUBLISH_DIR)/"

##@ Plugin Targets

plugin: plugin-example ## Build example plugin (alias)

plugin-example: ## Build example plugin
	dotnet build examples/ExamplePlugin/ExamplePlugin.csproj -c Release
	@echo "Example plugin built: examples/ExamplePlugin/bin/Release/net8.0/"

plugin-install: plugin-example ## Install example plugin to user plugins directory
	mkdir -p $(PLUGIN_DIR)/ExamplePlugin
	cp examples/ExamplePlugin/bin/Release/net8.0/ExamplePlugin.dll $(PLUGIN_DIR)/ExamplePlugin/
	cp examples/ExamplePlugin/bin/Release/net8.0/Modular.Sdk.dll $(PLUGIN_DIR)/ExamplePlugin/ 2>/dev/null || true
	cp examples/ExamplePlugin/plugin.json $(PLUGIN_DIR)/ExamplePlugin/ 2>/dev/null || true
	@echo "Example plugin installed to $(PLUGIN_DIR)/ExamplePlugin/"

plugin-clean: ## Remove installed plugins
	rm -rf $(PLUGIN_DIR)/*
	@echo "All plugins removed from $(PLUGIN_DIR)"

##@ SDK Targets

sdk-pack: ## Pack SDK as NuGet package
	dotnet pack src/Modular.Sdk/Modular.Sdk.csproj -c Release -o $(PUBLISH_DIR)/nuget
	@echo "SDK package created in $(PUBLISH_DIR)/nuget/"

##@ Utility Targets

help: ## Display this help message
	@awk 'BEGIN {FS = ":.*##"; printf "\nUsage:\n  make \033[36m<target>\033[0m\n"} /^[a-zA-Z_-]+:.*?##/ { printf "  \033[36m%-20s\033[0m %s\n", $$1, $$2 } /^##@/ { printf "\n\033[1m%s\033[0m\n", substr($$0, 5) } ' $(MAKEFILE_LIST)
