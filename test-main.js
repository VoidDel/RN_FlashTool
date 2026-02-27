// 简单测试脚本来验证main.js语法
console.log('Testing main.js syntax...');

try {
    require('./main.js');
    console.log('✓ main.js syntax is valid');
} catch (error) {
    console.error('✗ Error in main.js:', error.message);
    console.error('Line:', error.stack);
}