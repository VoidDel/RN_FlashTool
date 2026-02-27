# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is a PySide6-based GUI application for flashing STM32 microcontrollers using OpenOCD. The application provides a simple interface to:
- Select and flash firmware files (.bin or .elf formats) to STM32 targets
- Verify flashed firmware
- Reset connected devices
- Support various programming interfaces (ST-Link, J-Link, CMSIS-DAP)

## Development Environment Setup

1. **Python Environment**:
   ```bash
   # Activate virtual environment (if exists)
   source .venv/bin/activate  # On Windows: .venv\Scripts\activate

   # Install dependencies
   pip install PySide6
   ```

2. **OpenOCD Dependency**:
   - The application expects OpenOCD to be either:
     - Available in system PATH, or
     - Located in `./openocd/bin/openocd.exe` (Windows) or `./openocd/bin/openocd` (Unix)
   - The repository includes a pre-packaged OpenOCD distribution in the `openocd/` directory

## Running the Application

```bash
python stm32_flash.py
```

## Key Architecture Components

### Core Classes

1. **FlashManager** (`stm32_flash.py:54`):
   - Manages OpenOCD configuration and execution
   - Handles target chip detection and configuration file mapping
   - Implements flash, verify, and reset operations
   - Locates OpenOCD executable automatically

2. **FlashWorker** (`stm32_flash.py:186`):
   - Runs flash operations in a separate QThread to prevent UI blocking
   - Emits signals when operations complete

3. **FlashWindow** (`stm32_flash.py:207`):
   - Main GUI window built with PySide6
   - Manages UI state and user interactions
   - Coordinates with FlashManager and FlashWorker

### Target Configuration

The application supports STM32 families through the `STM32_TARGET_MAP` dictionary (`stm32_flash.py:30`):
- Maps chip prefixes to OpenOCD configuration files
- Supports F0, F1, F2, F3, F4, F7, H7, L0, L1, L4, G0, G4 series

### Temporary Files

- OpenOCD configuration files are generated in the `./temp/` directory
- Files are automatically cleaned up after operations

## Common Development Tasks

### Adding Support for New STM32 Families

1. Add an entry to `STM32_TARGET_MAP` (`stm32_flash.py:30`)
2. The key should be the lowercase chip prefix
3. The value should be the corresponding OpenOCD target config file path

### Modifying OpenOCD Commands

Edit the following methods in `FlashManager`:
- `flash()` (`stm32_flash.py:130`) - for flash operations
- `verify()` (`stm32_flash.py:141`) - for verification
- `reset()` (`stm32_flash.py:152`) - for reset commands

### Adding New Programming Interfaces

Update `cfg_header()` method (`stm32_flash.py:80`) in `FlashManager`:
1. Add a new elif block for the interface
2. Specify the appropriate interface config file and transport selection
3. Add the interface name to the dropdown in `_build_ui()` (`stm32_flash.py:249`)

## File Structure

```
STM32_FLASH_CLI/
├── stm32_flash.py    # Main application file
├── openocd/              # OpenOCD distribution
│   ├── bin/
│   │   └── openocd.exe
│   └── distro-info/
├── temp/                 # Temporary OpenOCD configs (created at runtime)
├── .venv/               # Python virtual environment
└── .claude/             # Claude Code settings
```

## Important Implementation Details

- All file paths are converted to forward slashes for OpenOCD compatibility
- The application uses Qt signals/slots for thread communication
- OpenOCD operations are non-blocking through the use of QThread
- Target chip detection is case-insensitive
- Default flash address is `0x08000000` (typical for STM32 devices)