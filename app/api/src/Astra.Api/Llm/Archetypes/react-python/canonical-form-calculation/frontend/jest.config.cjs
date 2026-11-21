/** Jest with ts-jest in a jsdom browser. */
module.exports = {
  testEnvironment: 'jsdom',
  transform: {
    '^.+\\.tsx?$': ['ts-jest', { isolatedModules: true, tsconfig: { jsx: 'react-jsx', esModuleInterop: true, types: ['jest'] } }],
  },
  testMatch: ['**/src/**/*.test.ts?(x)'],
};
