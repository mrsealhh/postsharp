cask "postsharp" do
  version "1.11.20"
  sha256 "12c5fc29a111794dc51fac8a0c0d6580a00ed860cefaa3d02977e528d2b770f2"

  url "https://github.com/mrsealhh/postsharp/releases/download/v#{version}/PostSharp-osx-arm64.zip"
  name "PostSharp"
  desc "Local API debugging workbench with MCP automation"
  homepage "https://github.com/mrsealhh/postsharp"

  depends_on arch: :arm64

  binary "PostSharp"
end