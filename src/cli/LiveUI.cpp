#include "LiveUI.h"
#include <iostream>
#include <iomanip>
#include <algorithm>

void LiveUI::begin() {
    // Reserve two lines we will repaint
    std::cout << "\n\n";
    redraw();
}

void LiveUI::setOperation(const std::string& op, int total) {
    op_ = op;
    total_ = std::max(0, total);
    current_ = 0;
    status_.clear();
    redraw();
}

void LiveUI::setStatus(const std::string& s) {
    status_ = s;
    redraw();
}

void LiveUI::setProgress(int current) {
    current_ = std::clamp(current, 0, total_);
    redraw();
}

void LiveUI::tick(int delta) {
    setProgress(current_ + delta);
}

void LiveUI::finish(const std::string& finalStatus) {
    if (!finalStatus.empty()) status_ = finalStatus;
    current_ = total_;
    redraw();
    std::cout << "\n";
    std::cout.flush();
}

void LiveUI::clearLine() {
    std::cout << "\r\033[2K";
}

std::string LiveUI::bar(int current, int total, int width) {
    if (total <= 0) total = 1;
    int filled = (width * current) / total;
    filled = std::clamp(filled, 0, width);
    return "[" + std::string(filled, '#') + std::string(width - filled, ' ') + "]";
}

void LiveUI::redraw() {
    // Move cursor up 2 lines
    std::cout << "\033[2A";

    // Line 1: bar + percent + op label
    clearLine();
    double pct = (total_ > 0) ? (100.0 * current_ / total_) : 0.0;
    std::cout << bar(current_, total_) << " "
              << std::fixed << std::setprecision(1)
              << pct << "% (" << current_ << "/" << total_ << ") "
              << op_ << "\n";

    // Line 2: single changing status line
    clearLine();
    std::cout << status_ << "\n";

    std::cout.flush();
}
