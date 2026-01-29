.PHONY: build release install uninstall clean test

BUILD_DIR = src/Modular.Cli/bin/Release/net8.0/linux-x64
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
	rm -rf src/*/bin src/*/obj tests/*/bin tests/*/obj

test:
	dotnet test Modular.sln
