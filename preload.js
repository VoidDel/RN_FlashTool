const { contextBridge, ipcRenderer } = require('electron');

// 暴露安全的API给渲染进程
contextBridge.exposeInMainWorld('electronAPI', {
    // 芯片列表
    getChipList: () => ipcRenderer.invoke('get-chip-list'),

    // 文件对话框
    selectFirmware: () => ipcRenderer.invoke('select-firmware'),
    saveFirmware: (defaultName) => ipcRenderer.invoke('save-firmware', defaultName),

    // 消息框
    showMessageBox: (options) => ipcRenderer.invoke('show-message-box', options),

    // OpenOCD操作
    flashFirmware: (params) => ipcRenderer.invoke('flash-firmware', params),
    verifyFirmware: (params) => ipcRenderer.invoke('verify-firmware', params),
    resetDevice: (params) => ipcRenderer.invoke('reset-device', params),
    readFirmware: (params) => ipcRenderer.invoke('read-firmware', params),

    // 事件监听
    onOpenOCDNotFound: (callback) => ipcRenderer.on('openocd-not-found', callback),
    onOpenOCDFound: (callback) => ipcRenderer.on('openocd-found', callback),

    // 移除监听器
    removeAllListeners: (channel) => ipcRenderer.removeAllListeners(channel)
});