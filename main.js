const { app, BrowserWindow, ipcMain, dialog, shell } = require('electron');
const path = require('path');
const { spawn, exec } = require('child_process');
const fs = require('fs').promises;
const os = require('os');

let mainWindow;
let openocdPath = '';

// OpenOCD配置映射
const STM32_TARGET_MAP = {
    "stm32f0": "target/stm32f0x.cfg",
    "stm32f1": "target/stm32f1x.cfg",
    "stm32f2": "target/stm32f2x.cfg",
    "stm32f3": "target/stm32f3x.cfg",
    "stm32f4": "target/stm32f4x.cfg",
    "stm32f7": "target/stm32f7x.cfg",
    "stm32h7": "target/stm32h7x.cfg",
    "stm32l0": "target/stm32l0.cfg",
    "stm32l1": "target/stm32l1.cfg",
    "stm32l4": "target/stm32l4x.cfg",
    "stm32g0": "target/stm32g0x.cfg",
    "stm32g4": "target/stm32g4x.cfg"
};

// 常用的STM32芯片型号列表
const STM32_CHIP_LIST = [
    { display: "STM32F0系列", value: "stm32f0" },
    { display: "STM32F1系列", value: "stm32f1" },
    { display: "STM32F103", value: "stm32f103" },
    { display: "STM32F2系列", value: "stm32f2" },
    { display: "STM32F3系列", value: "stm32f3" },
    { display: "STM32F4系列", value: "stm32f4" },
    { display: "STM32F401", value: "stm32f401" },
    { display: "STM32F405", value: "stm32f405" },
    { display: "STM32F407", value: "stm32f407" },
    { display: "STM32F411", value: "stm32f411" },
    { display: "STM32F429", value: "stm32f429" },
    { display: "STM32F469", value: "stm32f469" },
    { display: "STM32F7系列", value: "stm32f7" },
    { display: "STM32F746", value: "stm32f746" },
    { display: "STM32F767", value: "stm32f767" },
    { display: "STM32H7系列", value: "stm32h7" },
    { display: "STM32H743", value: "stm32h743" },
    { display: "STM32L0系列", value: "stm32l0" },
    { display: "STM32L073", value: "stm32l073" },
    { display: "STM32L1系列", value: "stm32l1" },
    { display: "STM32L152", value: "stm32l152" },
    { display: "STM32L4系列", value: "stm32l4" },
    { display: "STM32L432", value: "stm32l432" },
    { display: "STM32L476", value: "stm32l476" },
    { display: "STM32G0系列", value: "stm32g0" },
    { display: "STM32G070", value: "stm32g070" },
    { display: "STM32G4系列", value: "stm32g4" },
    { display: "STM32G431", value: "stm32g431" }
];

function createWindow() {
    mainWindow = new BrowserWindow({
        width: 1200,
        height: 800,
        minWidth: 900,
        minHeight: 600,
        webPreferences: {
            nodeIntegration: false,
            contextIsolation: true,
            enableRemoteModule: false,
            preload: path.join(__dirname, 'preload.js')
        },
        icon: path.join(__dirname, 'assets/icon.png'),
        show: false
    });

    mainWindow.loadFile('index.html');

    mainWindow.once('ready-to-show', () => {
        mainWindow.show();
        checkOpenOCD();
    });

    mainWindow.on('closed', () => {
        mainWindow = null;
    });
}

app.whenReady().then(createWindow);

app.on('window-all-closed', () => {
    if (process.platform !== 'darwin') {
        app.quit();
    }
});

app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) {
        createWindow();
    }
});

// 检查OpenOCD
async function checkOpenOCD() {
    const platform = os.platform();
    let openocdExec = 'openocd';

    // 检查本地目录
    const localPath = path.join(__dirname, 'openocd', platform === 'win32' ? 'bin/openocd.exe' : 'bin/openocd');

    try {
        await fs.access(localPath);
        openocdPath = localPath;
    } catch (error) {
        // 检查系统PATH
        try {
            await new Promise((resolve, reject) => {
                exec('openocd --version', (error, stdout, stderr) => {
                    if (!error) {
                        openocdPath = 'openocd';
                        resolve();
                    } else {
                        reject(error);
                    }
                });
            });
        } catch (error) {
            mainWindow.webContents.send('openocd-not-found');
            return;
        }
    }

    mainWindow.webContents.send('openocd-found', openocdPath);
}

// IPC处理程序
ipcMain.handle('get-chip-list', () => {
    return STM32_CHIP_LIST;
});

ipcMain.handle('select-firmware', async () => {
    const result = await dialog.showOpenDialog(mainWindow, {
        properties: ['openFile'],
        filters: [
            { name: '固件文件', extensions: ['elf', 'bin'] },
            { name: 'ELF文件', extensions: ['elf'] },
            { name: 'BIN文件', extensions: ['bin'] },
            { name: '所有文件', extensions: ['*'] }
        ]
    });
    return result;
});

ipcMain.handle('save-firmware', async (event, defaultName) => {
    const result = await dialog.showSaveDialog(mainWindow, {
        defaultPath: defaultName,
        filters: [
            { name: 'BIN文件', extensions: ['bin'] },
            { name: '所有文件', extensions: ['*'] }
        ]
    });
    return result;
});

ipcMain.handle('show-message-box', async (event, options) => {
    const result = await dialog.showMessageBox(mainWindow, options);
    return result.response;
});

// OpenOCD操作
async function runOpenOCD(config) {
    return new Promise((resolve, reject) => {
        const tempDir = path.join(__dirname, 'temp');
        const configPath = path.join(tempDir, `config_${Date.now()}.cfg`);

        // 确保temp目录存在
        fs.mkdir(tempDir, { recursive: true }).catch(() => {});

        // 写入配置文件
        fs.writeFile(configPath, config, 'utf8')
            .then(() => {
                const args = ['-f', configPath];
                const child = spawn(openocdPath, args, {
                    cwd: path.dirname(openocdPath)
                });

                let stdout = '';
                let stderr = '';

                child.stdout.on('data', (data) => {
                    stdout += data.toString();
                });

                child.stderr.on('data', (data) => {
                    stderr += data.toString();
                });

                child.on('close', (code) => {
                    // 删除临时配置文件
                    fs.unlink(configPath).catch(() => {});

                    const output = stdout + stderr;
                    const success = code === 0;

                    // 分析错误
                    let errorAnalysis = '';
                    if (!success) {
                        if (output.includes('cannot read IDR')) {
                            errorAnalysis = '\n\n⚠️ 连接错误分析：\n• 请检查CMSIS-DAP连接是否正常\n• 确认目标芯片已上电\n• 检查SWD接线（SWDIO、SWCLK、GND）\n• 尝试降低连接速度';
                        } else if (output.toLowerCase().includes('unknown chip') || output.toLowerCase().includes('unable to identify target')) {
                            errorAnalysis = '\n\n⚠️ 芯片识别错误：\n• 检查目标芯片型号选择是否正确\n• 确认芯片没有被锁定\n• 尝试使用复位引脚';
                        } else if (output.toLowerCase().includes('timeout')) {
                            errorAnalysis = '\n\n⚠️ 超时错误：\n• 检查硬件连接\n• 确认芯片供电正常\n• 尝试降低连接速度';
                        }
                    }

                    resolve({
                        success,
                        output: `--- OpenOCD输出 ---\n${output}\n--- 输出结束 ---${errorAnalysis}`
                    });
                });

                child.on('error', (error) => {
                    fs.unlink(configPath).catch(() => {});
                    reject(error);
                });

                // 设置超时
                setTimeout(() => {
                    child.kill();
                    fs.unlink(configPath).catch(() => {});
                    reject(new Error('OpenOCD操作超时（30秒）'));
                }, 30000);
            })
            .catch(reject);
    });
}

// IPC处理OpenOCD操作
ipcMain.handle('flash-firmware', async (event, params) => {
    try {
        const { firmwarePath, format, address, interface, targetChip } = params;

        // 查找目标配置
        let targetCfg = '';
        const chip = targetChip.toLowerCase();
        for (const [prefix, cfg] of Object.entries(STM32_TARGET_MAP)) {
            if (chip.startsWith(prefix)) {
                targetCfg = cfg;
                break;
            }
        }

        if (!targetCfg) {
            throw new Error(`无法识别的目标芯片: ${targetChip}`);
        }

        // 构建配置
        let config = '';
        if (interface === 'stlink') {
            config += `source [find interface/stlink.cfg]\n`;
            config += `transport select swd\n`;
        } else if (interface === 'jlink') {
            config += `source [find interface/jlink.cfg]\n`;
            config += `transport select swd\n`;
        } else if (interface === 'cmsis-dap') {
            config += `source [find interface/cmsis-dap.cfg]\n`;
            config += `transport select swd\n`;
        }

        config += `source [find ${targetCfg}]\n`;
        config += `reset_config srst_only srst_nogate\n\n`;

        if (format === 'bin') {
            config += `flash write_image erase "${firmwarePath.replace(/\\/g, '/')} ${address} bin\n`;
        } else {
            config += `flash write_image erase "${firmwarePath.replace(/\\/g, '/')}"\n`;
        }

        return await runOpenOCD(config);
    } catch (error) {
        return { success: false, output: error.message };
    }
});

ipcMain.handle('verify-firmware', async (event, params) => {
    try {
        const { firmwarePath, format, address, interface, targetChip } = params;

        let targetCfg = '';
        const chip = targetChip.toLowerCase();
        for (const [prefix, cfg] of Object.entries(STM32_TARGET_MAP)) {
            if (chip.startsWith(prefix)) {
                targetCfg = cfg;
                break;
            }
        }

        if (!targetCfg) {
            throw new Error(`无法识别的目标芯片: ${targetChip}`);
        }

        let config = '';
        if (interface === 'stlink') {
            config += `source [find interface/stlink.cfg]\n`;
            config += `transport select swd\n`;
        } else if (interface === 'jlink') {
            config += `source [find interface/jlink.cfg]\n`;
            config += `transport select swd\n`;
        } else if (interface === 'cmsis-dap') {
            config += `source [find interface/cmsis-dap.cfg]\n`;
            config += `transport select swd\n`;
        }

        config += `source [find ${targetCfg}]\n`;
        config += `reset_config srst_only srst_nogate\n\n`;

        if (format === 'bin') {
            config += `verify_image "${firmwarePath.replace(/\\/g, '/')} ${address} bin\n`;
        } else {
            config += `verify_image "${firmwarePath.replace(/\\/g, '/')}"\n`;
        }

        return await runOpenOCD(config);
    } catch (error) {
        return { success: false, output: error.message };
    }
});

ipcMain.handle('reset-device', async (event, params) => {
    try {
        const { interface, targetChip } = params;

        let targetCfg = '';
        const chip = targetChip.toLowerCase();
        for (const [prefix, cfg] of Object.entries(STM32_TARGET_MAP)) {
            if (chip.startsWith(prefix)) {
                targetCfg = cfg;
                break;
            }
        }

        if (!targetCfg) {
            throw new Error(`无法识别的目标芯片: ${targetChip}`);
        }

        let config = '';
        if (interface === 'stlink') {
            config += `source [find interface/stlink.cfg]\n`;
            config += `transport select swd\n`;
        } else if (interface === 'jlink') {
            config += `source [find interface/jlink.cfg]\n`;
            config += `transport select swd\n`;
        } else if (interface === 'cmsis-dap') {
            config += `source [find interface/cmsis-dap.cfg]\n`;
            config += `transport select swd\n`;
        }

        config += `source [find ${targetCfg}]\n`;
        config += `reset_config srst_only srst_nogate\n\n`;
        config += `reset run\n`;

        return await runOpenOCD(config);
    } catch (error) {
        return { success: false, output: error.message };
    }
});

ipcMain.handle('read-firmware', async (event, params) => {
    try {
        const { outputPath, address, size, interface, targetChip } = params;

        let targetCfg = '';
        const chip = targetChip.toLowerCase();
        for (const [prefix, cfg] of Object.entries(STM32_TARGET_MAP)) {
            if (chip.startsWith(prefix)) {
                targetCfg = cfg;
                break;
            }
        }

        if (!targetCfg) {
            throw new Error(`无法识别的目标芯片: ${targetChip}`);
        }

        let config = '';
        if (interface === 'stlink') {
            config += `source [find interface/stlink.cfg]\n`;
            config += `transport select swd\n`;
        } else if (interface === 'jlink') {
            config += `source [find interface/jlink.cfg]\n`;
            config += `transport select swd\n`;
        } else if (interface === 'cmsis-dap') {
            config += `source [find interface/cmsis-dap.cfg]\n`;
            config += `transport select swd\n`;
        }

        config += `source [find ${targetCfg}]\n`;
        config += `reset_config srst_only srst_nogate\n\n`;
        config += `dump_image "${outputPath.replace(/\\/g, '/')}" ${address} ${size}\n`;

        return await runOpenOCD(config);
    } catch (error) {
        return { success: false, output: error.message };
    }
});