package org.acme.shop;

import java.util.Map;

public final class DiscountPricing extends StandardPricing {
    private final int percent;

    public DiscountPricing(Map<String, Long> catalogue, int percent) {
        super(catalogue);
        this.percent = percent;
    }

    @Override
    protected long unitPrice(String sku) {
        return super.unitPrice(sku) * (100 - percent) / 100;
    }
}
