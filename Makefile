.PHONY: build release install uninstall clean test gui gui-run gui-publish-linux gui-publish-windows

BUILD_DIR = src/Modular.Cli/bin/Release/net8.0/linux-x64
GUI_BUILD_DIR = src/Modular.Gui/bin/Release/net8.0
PUBLISH_DIR = publish
INSTALL_DIR = $(HOME)/.local/bin

build:
	dotnet build Modular.sln

release:
	dotnet publish src/Modular.Cli/Modular.Cli.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

install: release
	mkdir -p $(INSTALL_DIR)
	cp $(BUILD_DIR)/publish/modular $(INSTALL_DIR)/modular

uninstall:
	rm -f $(INSTALL_DIR)/modular

clean:
	dotnet clean Modular.sln
	rm -rf src/*/bin src/*/obj tests/*/bin tests/*/obj $(PUBLISH_DIR)

test:
	dotnet test Modular.sln

# GUI targets
gui:
	dotnet build src/Modular.Gui/Modular.Gui.csproj -c Release

gui-run:
	dotnet run --project src/Modular.Gui/Modular.Gui.csproj

gui-publish-linux:
	mkdir -p $(PUBLISH_DIR)/gui-linux
	dotnet publish src/Modular.Gui/Modular.Gui.csproj -c Release -r linux-x64 --self-contained -o $(PUBLISH_DIR)/gui-linux

gui-publish-windows:
	mkdir -p $(PUBLISH_DIR)/gui-windows
	dotnet publish src/Modular.Gui/Modular.Gui.csproj -c Release -r win-x64 --self-contained -o $(PUBLISH_DIR)/gui-windows
