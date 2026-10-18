package com.fufucode.mobile

import android.app.Application

/**
 * 独立移动端 Application：仅承载进程级容器（会话存储、资源访问）。
 * 不引入任何腾讯 WorkBuddy 的凭据、账号或服务配置。
 */
class FufucodeMobileApp : Application()
