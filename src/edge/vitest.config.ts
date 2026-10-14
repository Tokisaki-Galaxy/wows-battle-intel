import { defineConfig } from "vitest/config";

/**
 * vitest 配置。
 *
 * 单元测试用内存替身注入 KV 与 D1 绑定，直接调用 worker.fetch，
 * 不依赖真实 Cloudflare 资源，因此在 Node 环境运行即可。
 *
 * 需要验证真实 workerd 行为时，可另建测试并切换为
 * @cloudflare/vitest-pool-workers 的 workers pool。
 */
export default defineConfig({
  test: {
    include: ["test/**/*.test.ts"],
    environment: "node",
  },
});
