# STM32 Flash Tool - Build Script
# Usage: .\build.ps1

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "STM32 Flash Tool - Build Script" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Check virtual environment
if (-not (Test-Path ".venv\Scripts\activate.bat")) {
    Write-Host "[ERROR] Virtual environment not found" -ForegroundColor Red
    Write-Host "Run: python -m venv .venv" -ForegroundColor Yellow
    Read-Host "Press Enter to exit"
    exit 1
}

# Activate virtual environment
Write-Host "[1/5] Activating virtual environment..." -ForegroundColor Green
& ".venv\Scripts\Activate.ps1"

# Check dependencies
Write-Host "[2/5] Checking dependencies..." -ForegroundColor Green
$pyinstaller = pip show pyinstaller 2>$null
if (-not $pyinstaller) {
    Write-Host "[INSTALL] Installing PyInstaller..." -ForegroundColor Yellow
    pip install pyinstaller
}

$pyside6 = pip show PySide6 2>$null
if (-not $pyside6) {
    Write-Host "[INSTALL] Installing PySide6..." -ForegroundColor Yellow
    pip install PySide6
}

# Clean old build files
Write-Host "[3/5] Cleaning old build files..." -ForegroundColor Green
if (Test-Path "build") {
    Remove-Item -Recurse -Force "build"
}
if (Test-Path "dist") {
    Remove-Item -Recurse -Force "dist"
}

# Build
Write-Host "[4/5] Building..." -ForegroundColor Green
pyinstaller stm32_flash.spec --clean --noconfirm

# Check result
if (-not (Test-Path "dist\STM32_Flash_Tool\STM32_Flash_Tool.exe")) {
    Write-Host ""
    Write-Host "[ERROR] Build failed!" -ForegroundColor Red
    Read-Host "Press Enter to exit"
    exit 1
}

# Copy documentation
Write-Host "[5/5] Copying documentation..." -ForegroundColor Green
if (Test-Path "README.md") {
    Copy-Item "README.md" "dist\STM32_Flash_Tool\"
}
if (Test-Path "CLAUDE.md") {
    Copy-Item "CLAUDE.md" "dist\STM32_Flash_Tool\"
}

# Show result
Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "Build Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "Output: dist\STM32_Flash_Tool\" -ForegroundColor Cyan
Write-Host "Executable: STM32_Flash_Tool.exe" -ForegroundColor Cyan
Write-Host ""

# Show size
$size = (Get-ChildItem -Path "dist\STM32_Flash_Tool" -Recurse | Measure-Object -Property Length -Sum).Sum
$sizeMB = [math]::Round($size / 1MB, 2)
Write-Host "Total Size: $sizeMB MB" -ForegroundColor Cyan
Write-Host ""

# Ask to open directory
$open = Read-Host "Open output directory? (Y/N)"
if ($open -eq "Y" -or $open -eq "y") {
    explorer "dist\STM32_Flash_Tool"
}

Write-Host ""
Write-Host "Tip: Use 7-Zip to compress the folder for distribution" -ForegroundColor Yellow
Read-Host "Press Enter to exit"
