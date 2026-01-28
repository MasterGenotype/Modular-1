#ifndef MODULAR_ILOGGER_H
#define MODULAR_ILOGGER_H

#include <string>
#include <iostream>
#include <chrono>
#include <iomanip>
#include <sstream>

namespace modular {

/**
 * @brief Logging interface to decouple core logic from UI/terminal output
 * 
 * This allows core/ components to log without depending on std::cout or terminal
 * CLI uses StderrLogger, GUI can use a panel logger, tests use a null logger
 */
class ILogger {
public:
    virtual ~ILogger() = default;
    
    virtual void debug(const std::string& msg) = 0;
    virtual void info(const std::string& msg) = 0;
    virtual void warn(const std::string& msg) = 0;
    virtual void error(const std::string& msg) = 0;
};

/**
 * @brief CLI logger that outputs to stderr with timestamps
 */
class StderrLogger : public ILogger {
public:
    explicit StderrLogger(bool show_debug = false) 
        : show_debug_(show_debug) {}
    
    void debug(const std::string& msg) override {
        if (show_debug_) {
            log("DEBUG", msg);
        }
    }
    
    void info(const std::string& msg) override {
        log("INFO", msg);
    }
    
    void warn(const std::string& msg) override {
        log("WARN", msg);
    }
    
    void error(const std::string& msg) override {
        log("ERROR", msg);
    }
    
private:
    bool show_debug_;
    
    void log(const char* level, const std::string& msg) {
        auto now = std::chrono::system_clock::now();
        auto time_t = std::chrono::system_clock::to_time_t(now);
        auto tm = *std::localtime(&time_t);
        
        std::ostringstream oss;
        oss << "[" << std::put_time(&tm, "%H:%M:%S") << "] "
            << "[" << level << "] " << msg << "\n";
        
        std::cerr << oss.str();
    }
};

/**
 * @brief Null logger for tests - discards all output
 */
class NullLogger : public ILogger {
public:
    void debug(const std::string&) override {}
    void info(const std::string&) override {}
    void warn(const std::string&) override {}
    void error(const std::string&) override {}
};

} // namespace modular

#endif // MODULAR_ILOGGER_H
