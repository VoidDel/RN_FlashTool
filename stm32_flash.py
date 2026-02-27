#!/usr/bin/env python3
"""PySide6 GUI for flashing STM32 targets via OpenOCD."""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
import tempfile
from typing import Tuple

from PySide6.QtCore import QObject, QThread, Signal, Slot, Qt
from PySide6.QtWidgets import (
    QApplication,
    QFileDialog,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QMainWindow,
    QMessageBox,
    QComboBox,
    QPlainTextEdit,
    QVBoxLayout,
    QWidget,
    QFormLayout,
    QProgressBar,
    QGridLayout,
    QPushButton,
    QGroupBox,
    QTextEdit,
)

STM32_TARGET_MAP = {
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
    "stm32g4": "target/stm32g4x.cfg",
    "stm32f10x": "target/stm32f1x.cfg",
    "stm32f103": "target/stm32f1x.cfg",
    "stm32f407": "target/stm32f4x.cfg",
    "stm32f401": "target/stm32f4x.cfg",
    "stm32f405": "target/stm32f4x.cfg",
    "stm32f411": "target/stm32f4x.cfg",
    "stm32f429": "target/stm32f4x.cfg",
    "stm32f469": "target/stm32f4x.cfg",
    "stm32f746": "target/stm32f7x.cfg",
    "stm32f767": "target/stm32f7x.cfg",
    "stm32h743": "target/stm32h7x.cfg",
    "stm32l476": "target/stm32l4x.cfg",
    "stm32l432": "target/stm32l4x.cfg",
    "stm32l073": "target/stm32l0.cfg",
    "stm32l152": "target/stm32l1.cfg",
    "stm32g431": "target/stm32g4x.cfg",
    "stm32g070": "target/stm32g0x.cfg",
}

# 常用的STM32芯片型号列表
STM32_CHIP_LIST = [
    ("STM32F0系列", "stm32f0"),
    ("STM32F1系列", "stm32f1"),
    ("STM32F103", "stm32f103"),
    ("STM32F2系列", "stm32f2"),
    ("STM32F3系列", "stm32f3"),
    ("STM32F4系列", "stm32f4"),
    ("STM32F401", "stm32f401"),
    ("STM32F405", "stm32f405"),
    ("STM32F407", "stm32f407"),
    ("STM32F411", "stm32f411"),
    ("STM32F429", "stm32f429"),
    ("STM32F469", "stm32f469"),
    ("STM32F7系列", "stm32f7"),
    ("STM32F746", "stm32f746"),
    ("STM32F767", "stm32f767"),
    ("STM32H7系列", "stm32h7"),
    ("STM32H743", "stm32h743"),
    ("STM32L0系列", "stm32l0"),
    ("STM32L073", "stm32l073"),
    ("STM32L1系列", "stm32l1"),
    ("STM32L152", "stm32l152"),
    ("STM32L4系列", "stm32l4"),
    ("STM32L432", "stm32l432"),
    ("STM32L476", "stm32l476"),
    ("STM32G0系列", "stm32g0"),
    ("STM32G070", "stm32g070"),
    ("STM32G4系列", "stm32g4"),
    ("STM32G431", "stm32g431"),
]


def detect_target_cfg(chip: str) -> str | None:
    chip = chip.lower()
    for prefix, cfg in STM32_TARGET_MAP.items():
        if chip.startswith(prefix):
            return cfg
    return None


class FlashManager:
    DEFAULT_ADDRESS = "0x08000000"

    def __init__(self) -> None:
        self.firmware: str = ""
        self.format: str = "elf"
        self.address: str = self.DEFAULT_ADDRESS
        self.interface: str = "cmsis-dap"
        self.target_chip: str = "stm32f4"
        self.target_cfg: str = "target/stm32f4x.cfg"
        self.openocd_path: str | None = self._locate_openocd()

    def _locate_openocd(self) -> str | None:
        found = shutil.which("openocd")
        if found:
            return found
        base = os.path.dirname(os.path.abspath(__file__))
        candidates = [
            os.path.join(base, "openocd", "bin", "openocd.exe"),
            os.path.join(base, "openocd", "bin", "openocd"),
        ]
        for path in candidates:
            if os.path.isfile(path):
                return path
        return None

    def cfg_header(self) -> str:
        if self.interface == "stlink":
            interface_lines = [
                "source [find interface/stlink.cfg]",
                "transport select hla_swd",
            ]
        elif self.interface == "jlink":
            interface_lines = [
                "source [find interface/jlink.cfg]",
                "transport select swd",
            ]
        elif self.interface == "cmsis-dap":
            interface_lines = [
                "source [find interface/cmsis-dap.cfg]",
                "transport select swd",
            ]
        else:
            raise ValueError("不支持的编程器接口")

        lines = interface_lines + [
            f"source [find {self.target_cfg}]",
            "reset_config srst_only",
            "init",
            "reset halt",
            "",
        ]
        return "\n".join(lines)

    def set_target_chip(self, chip: str) -> Tuple[bool, str]:
        chip = chip.strip()
        if not chip:
            return False, "目标芯片不能为空"
        cfg = detect_target_cfg(chip)
        if cfg is None:
            return False, f"不支持的目标芯片前缀。支持的系列: {', '.join(sorted(set(k.split('x')[0] + 'x' for k in STM32_TARGET_MAP.keys())))}"
        self.target_chip = chip
        self.target_cfg = cfg
        return True, f"已为 {chip} 选择配置文件 {cfg}"

    def _verify_common(self) -> Tuple[bool, str]:
        if not self.openocd_path:
            return False, "未找到 openocd 可执行文件。请安装 OpenOCD 或将其放置在 openocd/bin 目录下"
        if not self.firmware:
            return False, "固件路径为空"
        if not os.path.exists(self.firmware):
            return False, f"固件文件不存在: {self.firmware}"
        if self.format not in ("bin", "elf"):
            return False, "格式必须是 bin 或 elf"
        return True, ""

    def flash(self) -> Tuple[bool, str]:
        ok, msg = self._verify_common()
        if not ok:
            return ok, msg
        fw = os.path.abspath(self.firmware).replace("\\", "/")
        if self.format == "bin":
            cmd = f'flash write_image erase "{fw}" {self.address} bin'
        else:
            cmd = f'flash write_image erase "{fw}"'
        return self._run_openocd_cmds(cmd, "烧录")

    def verify(self) -> Tuple[bool, str]:
        ok, msg = self._verify_common()
        if not ok:
            return ok, msg
        fw = os.path.abspath(self.firmware).replace("\\", "/")
        if self.format == "bin":
            cmd = f'verify_image "{fw}" {self.address} bin'
        else:
            cmd = f'verify_image "{fw}"'
        return self._run_openocd_cmds(cmd, "验证")

    def reset(self) -> Tuple[bool, str]:
        if not self.openocd_path:
            return False, "未找到 openocd 可执行文件。请安装 OpenOCD 或将其放置在 openocd/bin 目录下"
        return self._run_openocd_cmds("reset run", "复位")

    def read(self, output_path: str, size: str = None) -> Tuple[bool, str]:
        """读取固件从芯片到文件"""
        if not self.openocd_path:
            return False, "未找到 openocd 可执行文件。请安装 OpenOCD 或将其放置在 openocd/bin 目录下"

        output_abs = os.path.abspath(output_path).replace("\\", "/")

        # 如果没有指定大小，使用默认64KB
        if not size:
            size = "0x10000"  # 64KB

        # 构建读取命令
        cmd = f'dump_image "{output_abs}" {self.address} {size}'
        return self._run_openocd_cmds(cmd, "读取")

    def _run_openocd_cmds(self, cmds: str, banner: str) -> Tuple[bool, str]:
        header = self.cfg_header()
        if not header.endswith("\n"):
            header += "\n"
        full_cfg = header + cmds + "\nshutdown\n"

        temp_dir = os.path.join(os.getcwd(), "temp")
        os.makedirs(temp_dir, exist_ok=True)

        with tempfile.NamedTemporaryFile("w", suffix=".cfg", delete=False, dir=temp_dir) as tf:
            tf.write(full_cfg)
            cfg_path = tf.name

        command = [self.openocd_path, "-f", cfg_path]
        try:
            # 添加调试信息
            config_content = f"\n--- OpenOCD配置内容 ---\n{full_cfg}\n--- 配置结束 ---\n"

            result = subprocess.run(command, capture_output=True, text=True, check=False, timeout=30)
            output = result.stdout + result.stderr
            success = result.returncode == 0

            # 分析常见错误
            error_analysis = ""
            if not success:
                if "cannot read IDR" in output:
                    error_analysis = "\n\n⚠️ 连接错误分析：\n• 请检查CMSIS-DAP连接是否正常\n• 确认目标芯片已上电\n• 检查SWD接线（SWDIO、SWCLK、GND）\n• 尝试降低连接速度"
                elif "unknown chip" in output.lower() or "unable to identify target" in output.lower():
                    error_analysis = "\n\n⚠️ 芯片识别错误：\n• 检查目标芯片型号选择是否正确\n• 确认芯片没有被锁定\n• 尝试使用复位引脚"
                elif "timeout" in output.lower():
                    error_analysis = "\n\n⚠️ 超时错误：\n• 检查硬件连接\n• 确认芯片供电正常\n• 尝试降低连接速度"

            log = f"[{banner}] 使用配置文件: {cfg_path}\n{config_content}\n--- OpenOCD输出 ---\n{output}\n--- 输出结束 ---{error_analysis}"
            return success, log
        except subprocess.TimeoutExpired:
            return False, f"[{banner}] OpenOCD操作超时（30秒），可能存在连接问题"
        except Exception as exc:
            return False, f"[{banner}] 运行 openocd 失败: {exc}"
        finally:
            try:
                os.remove(cfg_path)
            except FileNotFoundError:
                pass


class FlashWorker(QObject):
    finished = Signal(bool, str)
    progress = Signal(str)

    def __init__(self, manager: FlashManager, action: str, **kwargs) -> None:
        super().__init__()
        self.manager = manager
        self.action = action
        # 存储额外的参数，如输出路径、大小等
        self.output_path = kwargs.get("output_path", "")
        self.read_size = kwargs.get("read_size", "")

    @Slot()
    def run(self) -> None:
        if self.action == "flash":
            self.progress.emit("正在烧录固件，请稍候...")
            success, log = self.manager.flash()
        elif self.action == "verify":
            self.progress.emit("正在验证固件，请稍候...")
            success, log = self.manager.verify()
        elif self.action == "reset":
            self.progress.emit("正在复位设备...")
            success, log = self.manager.reset()
        elif self.action == "read":
            self.progress.emit("正在读取固件，请稍候...")
            success, log = self.manager.read(self.output_path, self.read_size)
        else:
            success, log = False, f"未知操作: {self.action}"
        self.finished.emit(success, log)


class FlashWindow(QMainWindow):
    def __init__(self) -> None:
        super().__init__()
        self.manager = FlashManager()
        self.worker_thread: QThread | None = None
        self.worker: FlashWorker | None = None

        self.setWindowTitle("STM32 固件烧录工具 v1.0")
        self.resize(900, 419)

        self._build_ui()

    def _build_ui(self) -> None:
        # 创建中央widget和主布局
        central_widget = QWidget()
        self.setCentralWidget(central_widget)
        main_layout = QVBoxLayout(central_widget)
        main_layout.setContentsMargins(10, 10, 10, 10)
        main_layout.setSpacing(10)

        # 创建网格布局
        grid_layout = QGridLayout()
        main_layout.addLayout(grid_layout)

        # 固件配置组
        firmware_group = QGroupBox("固件配置")
        firmware_layout = QFormLayout(firmware_group)

        self.firmware_edit = QLineEdit()
        self.firmware_edit.setPlaceholderText("请选择固件文件...")
        firmware_h_layout = QHBoxLayout()
        firmware_h_layout.addWidget(self.firmware_edit)
        browse_btn = QPushButton("浏览")
        browse_btn.clicked.connect(self._browse_firmware)
        firmware_h_layout.addWidget(browse_btn)
        firmware_layout.addRow("固件文件:", firmware_h_layout)

        self.format_combo = QComboBox()
        self.format_combo.addItems(["ELF", "BIN"])
        self.format_combo.setCurrentText("ELF")
        firmware_layout.addRow("格式:", self.format_combo)

        self.address_edit = QLineEdit(self.manager.address)
        self.address_edit.setPlaceholderText("例如: 0x08000000")
        firmware_layout.addRow("烧录地址:", self.address_edit)

        self.read_size_edit = QLineEdit("0x10000")
        self.read_size_edit.setPlaceholderText("例如: 0x10000 (64KB)")
        firmware_layout.addRow("读取大小:", self.read_size_edit)

        grid_layout.addWidget(firmware_group, 0, 0)

        # 目标配置组
        target_group = QGroupBox("目标配置")
        target_layout = QFormLayout(target_group)

        self.interface_combo = QComboBox()
        self.interface_combo.addItems(["ST-Link", "J-Link", "CMSIS-DAP"])
        self.interface_combo.setCurrentText("CMSIS-DAP")
        target_layout.addRow("编程器:", self.interface_combo)

        self.target_combo = QComboBox()
        self.target_combo.setEditable(True)
        for display_name, chip_id in STM32_CHIP_LIST:
            self.target_combo.addItem(display_name, chip_id)
        self.target_combo.setCurrentText("STM32F4系列")
        self.target_combo.currentTextChanged.connect(self._on_target_changed)
        target_layout.addRow("目标芯片:", self.target_combo)

        self.target_status = QLabel(f"配置文件: {self.manager.target_cfg}")
        target_layout.addRow("", self.target_status)

        grid_layout.addWidget(target_group, 1, 0)

        # 操作按钮组
        button_group = QGroupBox("操作")
        button_layout = QGridLayout(button_group)

        self.flash_btn = QPushButton("🔥 烧录固件")
        self.flash_btn.clicked.connect(lambda: self._handle_action("flash"))
        button_layout.addWidget(self.flash_btn, 0, 0)

        self.verify_btn = QPushButton("✓ 验证固件")
        self.verify_btn.clicked.connect(lambda: self._handle_action("verify"))
        button_layout.addWidget(self.verify_btn, 0, 1)

        self.read_btn = QPushButton("📥 读取固件")
        self.read_btn.clicked.connect(lambda: self._handle_action("read"))
        button_layout.addWidget(self.read_btn, 1, 0)

        self.reset_btn = QPushButton("🔄 复位设备")
        self.reset_btn.clicked.connect(lambda: self._handle_action("reset"))
        button_layout.addWidget(self.reset_btn, 1, 1)

        grid_layout.addWidget(button_group, 2, 0)

        # 进度条
        self.progress_bar = QProgressBar()
        self.progress_bar.setVisible(False)
        self.progress_bar.setRange(0, 0)  # 无限进度条
        grid_layout.addWidget(self.progress_bar, 3, 0)

        # 日志输出组
        log_group = QGroupBox("操作日志")
        log_layout = QVBoxLayout(log_group)

        self.log_view = QTextEdit()
        self.log_view.setReadOnly(True)
        self.log_view.setMinimumHeight(200)
        log_layout.addWidget(self.log_view)

        clear_log_btn = QPushButton("清除日志")
        clear_log_btn.clicked.connect(self.log_view.clear)
        log_layout.addWidget(clear_log_btn)

        grid_layout.addWidget(log_group, 0, 1, 4, 1)

        # 设置列拉伸
        grid_layout.setColumnStretch(0, 1)
        grid_layout.setColumnStretch(1, 1)

        # 状态栏
        self.statusBar().showMessage("就绪")

    def _browse_firmware(self) -> None:
        path, _ = QFileDialog.getOpenFileName(
            self,
            "选择固件文件",
            "",
            "固件文件 (*.elf *.bin);;ELF 文件 (*.elf);;BIN 文件 (*.bin);;所有文件 (*.*)"
        )
        if path:
            self.firmware_edit.setText(path)
            # 根据文件扩展名自动设置格式
            if path.lower().endswith('.bin'):
                self.format_combo.setCurrentText("BIN")
            elif path.lower().endswith('.elf'):
                self.format_combo.setCurrentText("ELF")

    def _on_target_changed(self, text: str) -> None:
        """当目标芯片选择改变时触发"""
        chip = text.strip()
        if not chip:
            return

        # 如果是从下拉列表选择的，获取对应的chip_id
        index = self.target_combo.findText(text)
        if index >= 0:
            chip_id = self.target_combo.itemData(index)
            if chip_id:
                chip = chip_id

        ok, msg = self.manager.set_target_chip(chip)
        self.append_log(f"[系统] {msg}")
        if ok:
            self.target_status.setText(f"配置文件: {self.manager.target_cfg}")
            self.statusBar().showMessage(f"目标芯片: {chip}")
        else:
            msg_box = QMessageBox(QMessageBox.Warning, "无效的目标芯片", msg, QMessageBox.Ok, self)
            msg_box.button(QMessageBox.Ok).setText("确定")
            msg_box.exec()
            # 恢复到之前的值
            self.target_combo.setCurrentText(self.manager.target_chip)

    def _handle_action(self, action: str) -> None:
        if not self._update_manager_from_ui():
            return

        # 确认操作
        if action in ["flash", "verify"] and not self.manager.firmware:
            msg_box = QMessageBox(QMessageBox.Warning, "警告", "请先选择固件文件！", QMessageBox.Ok, self)
            msg_box.button(QMessageBox.Ok).setText("确定")
            msg_box.exec()
            return

        if action == "flash":
            msg_box = QMessageBox(
                QMessageBox.Question,
                "确认烧录",
                f"确定要将 {os.path.basename(self.manager.firmware)} 烧录到 {self.manager.target_chip} 吗？",
                QMessageBox.Yes | QMessageBox.No,
                self
            )
            msg_box.button(QMessageBox.Yes).setText("确定")
            msg_box.button(QMessageBox.No).setText("取消")
            reply = msg_box.exec()
            if reply != QMessageBox.Yes:
                return
        elif action == "read":
            # 选择保存文件
            path, _ = QFileDialog.getSaveFileName(
                self,
                "保存读取的固件",
                f"read_{self.manager.target_chip}.bin",
                "BIN 文件 (*.bin);;所有文件 (*.*)"
            )
            if not path:
                return
            # 保存路径供worker使用
            self.read_output_path = path

        self._start_worker(action)

    def _update_manager_from_ui(self) -> bool:
        self.manager.firmware = self.firmware_edit.text().strip()
        self.manager.format = self.format_combo.currentText().lower()
        addr = self.address_edit.text().strip()
        self.manager.address = addr or self.manager.DEFAULT_ADDRESS

        # 获取接口值
        interface_map = {
            "ST-Link": "stlink",
            "J-Link": "jlink",
            "CMSIS-DAP": "cmsis-dap"
        }
        interface_text = self.interface_combo.currentText()
        self.manager.interface = interface_map.get(interface_text, "cmsis-dap")

        # 获取目标芯片
        target_text = self.target_combo.currentText().strip()
        target = target_text

        # 如果是下拉列表中的选项，获取对应的chip_id
        index = self.target_combo.findText(target_text)
        if index >= 0:
            chip_id = self.target_combo.itemData(index)
            if chip_id:
                target = chip_id

        ok, msg = self.manager.set_target_chip(target or self.manager.target_chip)
        if not ok:
            msg_box = QMessageBox(QMessageBox.Warning, "无效的目标芯片", msg, QMessageBox.Ok, self)
            msg_box.button(QMessageBox.Ok).setText("确定")
            msg_box.exec()
            return False
        self.target_status.setText(f"配置文件: {self.manager.target_cfg}")
        return True

    def _start_worker(self, action: str) -> None:
        if self.worker_thread:
            msg_box = QMessageBox(QMessageBox.Information, "忙碌", "操作正在进行中，请等待完成...", QMessageBox.Ok, self)
            msg_box.button(QMessageBox.Ok).setText("确定")
            msg_box.exec()
            return

        self._set_buttons_enabled(False)
        self.progress_bar.setVisible(True)

        action_names = {
            "flash": "烧录",
            "verify": "验证",
            "reset": "复位",
            "read": "读取"
        }

        self.append_log(f"[系统] 开始{action_names[action]}操作...")
        self.statusBar().showMessage(f"正在{action_names[action]}...")

        # 准备worker参数
        worker_kwargs = {}
        if action == "read":
            worker_kwargs["output_path"] = self.read_output_path
            worker_kwargs["read_size"] = self.read_size_edit.text().strip()

        self.worker_thread = QThread()
        self.worker = FlashWorker(self.manager, action, **worker_kwargs)
        self.worker.moveToThread(self.worker_thread)
        self.worker_thread.started.connect(self.worker.run)
        self.worker.finished.connect(self._on_worker_finished)
        self.worker.progress.connect(self._on_progress_update)
        self.worker.finished.connect(self.worker_thread.quit)
        self.worker.finished.connect(self.worker.deleteLater)
        self.worker_thread.finished.connect(self._cleanup_thread)
        self.worker_thread.start()

    def _cleanup_thread(self) -> None:
        if self.worker_thread:
            self.worker_thread.deleteLater()
        self.worker_thread = None
        self.worker = None
        self._set_buttons_enabled(True)
        self.progress_bar.setVisible(False)
        self.statusBar().showMessage("就绪")

    @Slot(str)
    def _on_progress_update(self, message: str) -> None:
        self.statusBar().showMessage(message)

    @Slot(bool, str)
    def _on_worker_finished(self, success: bool, log: str) -> None:
        status = "✅ 成功" if success else "❌ 失败"
        self.append_log(f"{status}\n{log}")

        if success:
            msg_box = QMessageBox(QMessageBox.Information, "操作完成", "操作成功完成！", QMessageBox.Ok, self)
            msg_box.button(QMessageBox.Ok).setText("确定")
            msg_box.exec()
        else:
            msg_box = QMessageBox(QMessageBox.Critical, "操作失败", f"操作失败！\n请查看日志了解详情。", QMessageBox.Ok, self)
            msg_box.button(QMessageBox.Ok).setText("确定")
            msg_box.exec()

    def _set_buttons_enabled(self, enabled: bool) -> None:
        self.flash_btn.setEnabled(enabled)
        self.verify_btn.setEnabled(enabled)
        self.read_btn.setEnabled(enabled)
        self.reset_btn.setEnabled(enabled)

    def append_log(self, text: str) -> None:
        from datetime import datetime
        timestamp = datetime.now().strftime("%H:%M:%S")
        self.log_view.append(f"[{timestamp}] {text.strip()}")


def main() -> None:
    app = QApplication(sys.argv)
    app.setApplicationName("STM32 固件烧录工具")
    app.setOrganizationName("STM32 Flash Tool")

    # 使用系统默认字体，不加载自定义字体
    print("使用系统默认字体")

    # 设置全局样式，调整对话框按钮大小
    app.setStyleSheet("""
        QMessageBox QPushButton {
            min-width: 75px;
            min-height: 30px;
            font-size: 13px;
            font-weight: bold;
            padding: 4px 12px;
        }
        QMessageBox {
            font-size: 13px;
        }
    """)

    window = FlashWindow()
    window.show()

    # 检查 OpenOCD
    if not window.manager.openocd_path:
        msg_box = QMessageBox(
            QMessageBox.Warning,
            "警告",
            "未找到 OpenOCD！\n\n请确保：\n"
            "1. 已安装 OpenOCD 并添加到系统 PATH\n"
            "2. 或将 OpenOCD 可执行文件放在 openocd/bin/ 目录下",
            QMessageBox.Ok,
            window
        )
        msg_box.button(QMessageBox.Ok).setText("确定")
        msg_box.exec()

    sys.exit(app.exec())


if __name__ == "__main__":
    main()
